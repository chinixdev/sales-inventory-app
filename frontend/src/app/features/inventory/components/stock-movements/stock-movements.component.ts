import { ScrollRegionDirective } from '../../../../shared/directives/scroll-region.directive';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AppDatePipe } from '../../../../shared/pipes/app-date.pipe';
import { FormsModule } from '@angular/forms';
import { StockMovement, StockMovementType } from '../../models/stock-movement.model';
import { Product } from '../../../products/models/product.model';
import { InventoryApiService } from '../../../../core/services/inventory-api.service';
import { ProductApiService } from '../../../../core/services/product-api.service';
import { AuthService } from '../../../../core/services/auth.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';
import { StateMessageComponent } from '../../../../shared/components/state-message/state-message.component';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

type OperationType = 'entry' | 'adjustment';

const typeLabels: Record<StockMovementType, string> = {
  IN: 'Entrada',
  OUT: 'Salida',
  ADJUSTMENT: 'Ajuste'
};

@Component({
  selector: 'app-stock-movements',
  standalone: true,
  imports: [ScrollRegionDirective, AppDatePipe, FormsModule, StateMessageComponent, ConfirmDialogComponent],
  templateUrl: './stock-movements.component.html',
  styleUrls: ['./stock-movements.component.scss']
})
export class StockMovementsComponent implements OnInit {
  private readonly inventoryApi = inject(InventoryApiService);
  private readonly productApi = inject(ProductApiService);
  private readonly auth = inject(AuthService);

  /** Solo Admin registra entradas y ajustes; la API lo vuelve a validar. */
  protected readonly canWrite = computed(() => this.auth.hasRole('Admin'));

  // Datos y estado de la lista
  protected readonly movements = signal<StockMovement[]>([]);
  protected readonly products = signal<Product[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly loadError = signal('');
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly productFilter = signal<number | null>(null);
  protected readonly pageSize = 20;

  // Formulario
  protected readonly operation = signal<OperationType>('entry');
  protected readonly productId = signal<number | null>(null);
  protected readonly quantity = signal<number | null>(1);
  protected readonly newStock = signal<number | null>(null);
  protected readonly reason = signal('');
  protected readonly currentProduct = signal<Product | null>(null);
  protected readonly isCheckingStock = signal(false);
  protected readonly isSaving = signal(false);
  protected readonly confirmingAdjustment = signal(false);
  protected readonly actionError = signal('');
  protected readonly successMessage = signal('');

  /** Diferencia que produciría el ajuste respecto al stock actual verificado. */
  protected readonly adjustmentDelta = computed(() => {
    const product = this.currentProduct();
    const target = this.newStock();
    return product && target !== null ? target - product.stock : null;
  });

  protected readonly adjustmentMessage = computed(() => {
    const product = this.currentProduct();
    const delta = this.adjustmentDelta();
    if (!product || delta === null) {
      return '';
    }

    const sign = delta > 0 ? '+' : '';
    return `«${product.name}»: stock actual ${product.stock} → nuevo stock ${this.newStock()} (${sign}${delta}).`;
  });

  ngOnInit(): void {
    this.loadProducts();
    this.loadMovements();
  }

  protected typeLabel(type: StockMovementType): string {
    return typeLabels[type] ?? type;
  }

  protected loadProducts(): void {
    this.productApi.getPaged({ pageNumber: 1, pageSize: 100, activeOnly: true }).subscribe({
      next: result => this.products.set(result.items),
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudieron cargar los productos.'))
    });
  }

  protected loadMovements(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    this.inventoryApi.getMovements({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      productId: this.productFilter() ?? undefined
    }).subscribe({
      next: result => {
        this.movements.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar los movimientos.'));
        this.isLoading.set(false);
      }
    });
  }

  protected applyFilter(): void {
    this.pageNumber.set(1);
    this.loadMovements();
  }

  protected clearFilter(): void {
    this.productFilter.set(null);
    this.applyFilter();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.loadMovements();
    }
  }

  protected setOperation(operation: OperationType): void {
    this.operation.set(operation);
    this.actionError.set('');
  }

  /** Al elegir producto se consulta su stock vigente en la API (no el de la lista en caché). */
  protected onProductChange(id: number | null): void {
    this.productId.set(id);
    this.currentProduct.set(null);
    this.actionError.set('');
    if (id !== null) {
      this.refreshCurrentProduct(id);
    }
  }

  protected submit(): void {
    if (this.isSaving() || this.isCheckingStock() || !this.canWrite() || this.productId() === null) {
      return;
    }

    this.actionError.set('');
    this.successMessage.set('');
    if (this.operation() === 'adjustment') {
      // Muestra el stock vigente antes de confirmar.
      this.confirmingAdjustment.set(true);
      return;
    }

    this.sendRequest();
  }

  protected confirmAdjustment(): void {
    this.confirmingAdjustment.set(false);
    this.sendRequest();
  }

  private sendRequest(): void {
    const productId = this.productId();
    if (productId === null || !this.canWrite()) {
      return;
    }

    this.isSaving.set(true);
    const reason = this.reason().trim();
    const request = this.operation() === 'entry'
      ? this.inventoryApi.registerEntry({ productId, quantity: this.quantity() ?? 0, reason })
      : this.inventoryApi.registerAdjustment({ productId, newStock: this.newStock() ?? 0, reason });

    request.subscribe({
      next: movement => {
        this.successMessage.set(
          `${typeLabels[movement.movementType] ?? movement.movementType} registrada. Stock: ${movement.previousStock} → ${movement.newStock}.`
        );
        this.isSaving.set(false);
        this.resetForm(productId);
        this.pageNumber.set(1);
        this.loadMovements();
        this.loadProducts();
        this.refreshCurrentProduct(productId);
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo registrar el movimiento.'));
        this.isSaving.set(false);
        // Ante un conflicto (p. ej. concurrencia) el stock mostrado puede estar desactualizado.
        if (error?.status === 409) {
          this.refreshCurrentProduct(productId);
          this.loadMovements();
        }
      }
    });
  }

  private refreshCurrentProduct(id: number): void {
    this.isCheckingStock.set(true);
    this.productApi.getById(id).subscribe({
      next: product => {
        if (this.productId() === id) {
          this.currentProduct.set(product);
        }
        this.isCheckingStock.set(false);
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo consultar el stock actual del producto.'));
        this.isCheckingStock.set(false);
      }
    });
  }

  private resetForm(keepProductId: number): void {
    this.productId.set(keepProductId);
    this.quantity.set(1);
    this.newStock.set(null);
    this.reason.set('');
  }
}
