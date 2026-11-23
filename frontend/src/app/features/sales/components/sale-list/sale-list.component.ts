import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Sale } from '../../models/sale.model';
import { Product } from '../../../products/models/product.model';
import { ProductApiService } from '../../../../core/services/product-api.service';
import { SalesApiService } from '../../../../core/services/sales-api.service';
import { PagedResult } from '../../../../core/models/paged-result.model';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';

interface SaleItemFormState {
  productId: number | null;
  quantity: number;
}

@Component({
  selector: 'app-sale-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './sale-list.component.html',
  styleUrls: ['./sale-list.component.scss']
})
export class SaleListComponent implements OnInit {
  private readonly salesApi = inject(SalesApiService);
  private readonly productApi = inject(ProductApiService);

  sales: Sale[] = [];
  products: Product[] = [];
  isLoading = false;
  isSaving = false;
  errorMessage = '';
  successMessage = '';
  pageNumber = 1;
  readonly pageSize = 20;
  totalItems = 0;
  totalPages = 0;
  userIdFilter: number | null = null;
  fromDate = '';
  toDate = '';
  saleItems: SaleItemFormState[] = [{ productId: null, quantity: 1 }];
  selectedSale: Sale | null = null;

  ngOnInit(): void {
    this.loadProducts();
    this.loadSales();
  }

  loadProducts(): void {
    this.productApi.getPaged({ pageNumber: 1, pageSize: 100, activeOnly: true }).subscribe({
      next: result => this.products = result.items,
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar los productos.')
    });
  }

  loadSales(): void {
    this.isLoading = true;
    this.errorMessage = '';
    this.salesApi.getPaged({
      pageNumber: this.pageNumber,
      pageSize: this.pageSize,
      userId: this.userIdFilter ?? undefined,
      from: this.fromDate ? `${this.fromDate}T00:00:00` : undefined,
      to: this.toDate ? `${this.toDate}T23:59:59.999` : undefined
    }).subscribe({
      next: (result: PagedResult<Sale>) => {
        this.sales = result.items;
        this.totalItems = result.totalItems;
        this.totalPages = result.totalPages;
        this.isLoading = false;
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar las ventas.');
        this.isLoading = false;
      }
    });
  }

  applyFilters(): void {
    this.pageNumber = 1;
    this.loadSales();
  }

  changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages) {
      this.pageNumber = page;
      this.loadSales();
    }
  }

  addSaleItem(): void {
    this.saleItems.push({ productId: null, quantity: 1 });
  }

  removeSaleItem(index: number): void {
    if (this.saleItems.length > 1) {
      this.saleItems.splice(index, 1);
    }
  }

  createSale(): void {
    if (this.isSaving) {
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';
    this.salesApi.create({
      items: this.saleItems.map(item => ({ productId: item.productId ?? 0, quantity: item.quantity }))
    }).subscribe({
      next: sale => {
        this.successMessage = `Venta #${sale.id} registrada por ${sale.total.toFixed(2)}.`;
        this.selectedSale = sale;
        this.isSaving = false;
        this.saleItems = [{ productId: null, quantity: 1 }];
        this.pageNumber = 1;
        this.loadSales();
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo registrar la venta.');
        this.isSaving = false;
      }
    });
  }

  showDetails(id: number): void {
    if (this.selectedSale?.id === id) {
      this.selectedSale = null;
      return;
    }

    this.salesApi.getById(id).subscribe({
      next: sale => this.selectedSale = sale,
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudo cargar el detalle de la venta.')
    });
  }
}
