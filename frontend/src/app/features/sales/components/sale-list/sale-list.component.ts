import { ScrollRegionDirective } from '../../../../shared/directives/scroll-region.directive';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { PenPipe, formatPen } from '../../../../shared/pipes/pen.pipe';
import { AppDatePipe } from '../../../../shared/pipes/app-date.pipe';
import { FormsModule } from '@angular/forms';
import { catchError, forkJoin, of } from 'rxjs';
import { Sale } from '../../models/sale.model';
import { Product } from '../../../products/models/product.model';
import { ProductApiService } from '../../../../core/services/product-api.service';
import { SalesApiService } from '../../../../core/services/sales-api.service';
import { generateUuid } from '../../../../core/utils/uuid';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';
import { StateMessageComponent } from '../../../../shared/components/state-message/state-message.component';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

/** Línea del carrito. Los datos del producto son una instantánea para mostrar; el backend decide precio y total. */
export interface CartLine {
  productId: number;
  sku: string;
  name: string;
  unitPrice: number;
  stock: number;
  quantity: number;
}

const toCents = (value: number): number => Math.round(value * 100) / 100;

@Component({
  selector: 'app-sale-list',
  standalone: true,
  imports: [ScrollRegionDirective, PenPipe, AppDatePipe, FormsModule, StateMessageComponent, ConfirmDialogComponent],
  templateUrl: './sale-list.component.html',
  styleUrls: ['./sale-list.component.scss']
})
export class SaleListComponent implements OnInit {
  private readonly salesApi = inject(SalesApiService);
  private readonly productApi = inject(ProductApiService);

  // Listado de ventas
  protected readonly sales = signal<Sale[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly loadError = signal('');
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly selectedSale = signal<Sale | null>(null);
  protected readonly isLoadingDetail = signal(false);
  protected readonly pageSize = 20;
  protected readonly userIdFilter = signal<number | null>(null);
  protected readonly fromDate = signal('');
  protected readonly toDate = signal('');
  protected readonly hasFilters = computed(
    () => this.userIdFilter() !== null || !!this.fromDate() || !!this.toDate()
  );

  // Selector de productos
  protected readonly products = signal<Product[]>([]);
  protected readonly productSearch = signal('');
  protected readonly isLoadingProducts = signal(false);
  protected readonly productsError = signal('');

  // Carrito y registro
  protected readonly cart = signal<CartLine[]>([]);
  protected readonly isSaving = signal(false);
  protected readonly confirming = signal(false);
  protected readonly actionError = signal('');
  protected readonly notice = signal('');
  protected readonly createdSale = signal<Sale | null>(null);

  protected readonly estimatedTotal = computed(() =>
    toCents(this.cart().reduce((sum, line) => sum + toCents(line.unitPrice * line.quantity), 0))
  );
  protected readonly cartIssues = computed(() => this.cart().map(line => this.lineIssue(line)));
  protected readonly canSubmit = computed(
    () => this.cart().length > 0 && this.cartIssues().every(issue => issue === null) && !this.isSaving()
  );
  protected readonly confirmMessage = computed(() => {
    const units = this.cart().reduce((sum, line) => sum + (Number.isFinite(line.quantity) ? line.quantity : 0), 0);
    const total = formatPen(this.estimatedTotal());
    return `Se registrará una venta de ${this.cart().length} producto(s) y ${units} unidad(es). `
      + `Total estimado: ${total}. El total definitivo lo calcula el servidor.`;
  });

  ngOnInit(): void {
    this.loadProducts();
    this.loadSales();
  }

  // ---------- Ventas ----------
  protected loadSales(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    this.salesApi.getPaged({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      userId: this.userIdFilter() ?? undefined,
      from: this.fromDate() ? `${this.fromDate()}T00:00:00` : undefined,
      to: this.toDate() ? `${this.toDate()}T23:59:59.999` : undefined
    }).subscribe({
      next: result => {
        this.sales.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar las ventas.'));
        this.isLoading.set(false);
      }
    });
  }

  protected applyFilters(): void {
    this.pageNumber.set(1);
    this.loadSales();
  }

  protected clearFilters(): void {
    this.userIdFilter.set(null);
    this.fromDate.set('');
    this.toDate.set('');
    this.applyFilters();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.loadSales();
    }
  }

  protected showDetails(id: number): void {
    if (this.selectedSale()?.id === id) {
      this.selectedSale.set(null);
      return;
    }

    this.isLoadingDetail.set(true);
    this.salesApi.getById(id).subscribe({
      next: sale => {
        this.selectedSale.set(sale);
        this.isLoadingDetail.set(false);
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo cargar el detalle de la venta.'));
        this.isLoadingDetail.set(false);
      }
    });
  }

  // ---------- Productos ----------
  protected loadProducts(): void {
    this.isLoadingProducts.set(true);
    this.productsError.set('');
    this.productApi.getPaged({
      pageNumber: 1,
      pageSize: 20,
      activeOnly: true,
      searchTerm: this.productSearch().trim() || undefined
    }).subscribe({
      next: result => {
        this.products.set(result.items);
        this.isLoadingProducts.set(false);
      },
      error: error => {
        this.productsError.set(getApiErrorMessage(error, 'No se pudieron cargar los productos.'));
        this.isLoadingProducts.set(false);
      }
    });
  }

  // ---------- Idempotencia ----------

  /** Clave de la operación en curso; solo en memoria (nunca en localStorage ni sessionStorage). */
  private pendingIdempotencyKey: string | null = null;
  private pendingCartFingerprint: string | null = null;

  /**
   * Reutiliza la clave mientras el carrito sea exactamente el mismo (reintento tras un fallo de red o una
   * respuesta incierta). Si el contenido cambió, la clave anterior se descarta y se genera otra.
   */
  private resolveIdempotencyKey(): string {
    const fingerprint = this.cart()
      .map(line => `${line.productId}:${line.quantity}`)
      .sort()
      .join(';');

    if (!this.pendingIdempotencyKey || this.pendingCartFingerprint !== fingerprint) {
      this.pendingIdempotencyKey = generateUuid();
      this.pendingCartFingerprint = fingerprint;
    }

    return this.pendingIdempotencyKey;
  }

  // ---------- Carrito ----------
  protected inCart(productId: number): boolean {
    return this.cart().some(line => line.productId === productId);
  }

  protected addToCart(product: Product): void {
    this.actionError.set('');
    this.notice.set('');
    if (product.stock < 1) {
      return;
    }

    const existing = this.cart().find(line => line.productId === product.id);
    if (existing) {
      // Sin duplicados: se incrementa la línea existente hasta el stock disponible.
      this.setQuantity(product.id, Math.min(existing.quantity + 1, existing.stock));
      this.notice.set(`«${product.name}» ya estaba en el carrito; se ajustó su cantidad.`);
      return;
    }

    this.cart.update(lines => [...lines, {
      productId: product.id,
      sku: product.sku,
      name: product.name,
      unitPrice: product.price,
      stock: product.stock,
      quantity: 1
    }]);
  }

  protected setQuantity(productId: number, value: unknown): void {
    const quantity = typeof value === 'number' ? value : Number(value);
    this.cart.update(lines =>
      lines.map(line => line.productId === productId ? { ...line, quantity } : line)
    );
  }

  protected removeFromCart(productId: number): void {
    this.cart.update(lines => lines.filter(line => line.productId !== productId));
  }

  protected clearCart(): void {
    this.cart.set([]);
  }

  protected lineSubtotal(line: CartLine): number {
    return Number.isFinite(line.quantity) ? toCents(line.unitPrice * line.quantity) : 0;
  }

  private lineIssue(line: CartLine): string | null {
    if (!Number.isInteger(line.quantity) || line.quantity < 1) {
      return 'La cantidad debe ser un entero mayor a 0.';
    }

    if (line.quantity > line.stock) {
      return `Stock disponible: ${line.stock}.`;
    }

    return null;
  }

  protected requestSale(): void {
    this.actionError.set('');
    this.notice.set('');
    if (this.canSubmit()) {
      // La clave se crea al iniciar la confirmación de una operación nueva.
      this.resolveIdempotencyKey();
      this.confirming.set(true);
    }
  }

  protected confirmSale(): void {
    this.confirming.set(false);
    // Doble clic / reenvío: mientras hay una petición en curso no se envía otra.
    if (this.isSaving() || !this.canSubmit()) {
      return;
    }

    this.isSaving.set(true);
    const idempotencyKey = this.resolveIdempotencyKey();
    // El contrato real solo admite items; el vendedor lo determina el JWT en el backend.
    this.salesApi.create({
      items: this.cart().map(line => ({ productId: line.productId, quantity: line.quantity }))
    }, idempotencyKey).subscribe({
      next: sale => {
        // Venta confirmada: la operación terminó, la clave ya no se reutiliza.
        this.pendingIdempotencyKey = null;
        this.pendingCartFingerprint = null;
        this.createdSale.set(sale);
        this.selectedSale.set(null);
        this.cart.set([]);
        this.isSaving.set(false);
        this.pageNumber.set(1);
        this.loadSales();
        this.loadProducts();
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo registrar la venta.'));
        this.isSaving.set(false);
        // Stock insuficiente (400) o conflicto de concurrencia (409): las existencias pudieron cambiar.
        if (error?.status === 400 || error?.status === 409) {
          this.refreshCartStock();
          this.loadProducts();
        }
      }
    });
  }

  protected dismissCreated(): void {
    this.createdSale.set(null);
  }

  /** Vuelve a consultar el stock real de cada producto del carrito. */
  private refreshCartStock(): void {
    const lines = this.cart();
    if (lines.length === 0) {
      return;
    }

    forkJoin(lines.map(line => this.productApi.getById(line.productId).pipe(catchError(() => of(null)))))
      .subscribe(results => {
        const fresh = new Map(results.filter((p): p is Product => p !== null).map(p => [p.id, p]));
        this.cart.update(current => current.map(line => {
          const product = fresh.get(line.productId);
          return product ? { ...line, stock: product.stock, unitPrice: product.price } : line;
        }));
      });
  }
}
