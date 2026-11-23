import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { StockMovement } from '../../models/stock-movement.model';
import { Product } from '../../../products/models/product.model';
import { InventoryApiService } from '../../../../core/services/inventory-api.service';
import { ProductApiService } from '../../../../core/services/product-api.service';
import { PagedResult } from '../../../../core/models/paged-result.model';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';

interface MovementFormState {
  productId: number | null;
  quantity: number;
  newStock: number;
  reason: string;
}

@Component({
  selector: 'app-stock-movements',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './stock-movements.component.html',
  styleUrls: ['./stock-movements.component.scss']
})
export class StockMovementsComponent implements OnInit {
  private readonly inventoryApi = inject(InventoryApiService);
  private readonly productApi = inject(ProductApiService);

  movements: StockMovement[] = [];
  products: Product[] = [];
  isLoading = false;
  isSaving = false;
  errorMessage = '';
  successMessage = '';
  pageNumber = 1;
  readonly pageSize = 20;
  totalItems = 0;
  totalPages = 0;
  productFilter: number | null = null;
  movementType: 'entry' | 'adjustment' = 'entry';
  form = this.emptyForm();

  ngOnInit(): void {
    this.loadProducts();
    this.loadMovements();
  }

  loadProducts(): void {
    this.productApi.getPaged({ pageNumber: 1, pageSize: 100 }).subscribe({
      next: result => this.products = result.items,
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar los productos.')
    });
  }

  loadMovements(): void {
    this.isLoading = true;
    this.errorMessage = '';
    this.inventoryApi.getMovements({
      pageNumber: this.pageNumber,
      pageSize: this.pageSize,
      productId: this.productFilter ?? undefined
    }).subscribe({
      next: (result: PagedResult<StockMovement>) => {
        this.movements = result.items;
        this.totalItems = result.totalItems;
        this.totalPages = result.totalPages;
        this.isLoading = false;
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar los movimientos.');
        this.isLoading = false;
      }
    });
  }

  applyFilter(): void {
    this.pageNumber = 1;
    this.loadMovements();
  }

  changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages) {
      this.pageNumber = page;
      this.loadMovements();
    }
  }

  submitMovement(): void {
    if (this.isSaving) {
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';
    const request = this.movementType === 'entry'
      ? this.inventoryApi.registerEntry({
          productId: this.form.productId ?? 0,
          quantity: this.form.quantity,
          reason: this.form.reason
        })
      : this.inventoryApi.registerAdjustment({
          productId: this.form.productId ?? 0,
          newStock: this.form.newStock,
          reason: this.form.reason
        });

    request.subscribe({
      next: movement => {
        this.successMessage = `${movement.movementType} registrado. Stock: ${movement.previousStock} → ${movement.newStock}.`;
        this.isSaving = false;
        this.form = this.emptyForm();
        this.pageNumber = 1;
        this.loadMovements();
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo registrar el movimiento.');
        this.isSaving = false;
      }
    });
  }

  private emptyForm(): MovementFormState {
    return { productId: null, quantity: 1, newStock: 0, reason: '' };
  }
}
