import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Product } from '../../models/product.model';
import { Category } from '../../../categories/models/category.model';
import { CategoryApiService } from '../../../../core/services/category-api.service';
import { CreateProductRequest, ProductApiService, UpdateProductRequest } from '../../../../core/services/product-api.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';

interface ProductFormState {
  categoryId: number | null;
  sku: string;
  name: string;
  description: string;
  price: number;
  initialStock: number;
  minimumStock: number;
  isActive: boolean;
}

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './product-list.component.html',
  styleUrls: ['./product-list.component.scss']
})
export class ProductListComponent implements OnInit {
  private readonly productApi = inject(ProductApiService);
  private readonly categoryApi = inject(CategoryApiService);

  products: Product[] = [];
  categories: Category[] = [];
  isLoading = false;
  isSaving = false;
  errorMessage = '';
  successMessage = '';
  showForm = false;
  editingProductId: number | null = null;
  pageNumber = 1;
  readonly pageSize = 10;
  totalItems = 0;
  totalPages = 0;
  searchTerm = '';
  categoryFilter: number | null = null;
  activeOnly: boolean | null = null;
  lowStockOnly = false;
  form = this.emptyForm();

  ngOnInit(): void {
    this.loadCategories();
    this.loadProducts();
  }

  loadCategories(): void {
    this.categoryApi.getAll(true).subscribe({
      next: categories => this.categories = categories,
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar las categorías.')
    });
  }

  loadProducts(): void {
    this.isLoading = true;
    this.errorMessage = '';
    this.productApi.getPaged({
      pageNumber: this.pageNumber,
      pageSize: this.pageSize,
      searchTerm: this.searchTerm.trim() || undefined,
      categoryId: this.categoryFilter ?? undefined,
      activeOnly: this.activeOnly ?? undefined,
      lowStockOnly: this.lowStockOnly || undefined
    }).subscribe({
      next: result => {
        this.products = result.items;
        this.totalItems = result.totalItems;
        this.totalPages = result.totalPages;
        this.isLoading = false;
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar los productos.');
        this.isLoading = false;
      }
    });
  }

  applyFilters(): void {
    this.pageNumber = 1;
    this.loadProducts();
  }

  changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages) {
      this.pageNumber = page;
      this.loadProducts();
    }
  }

  startCreate(): void {
    this.editingProductId = null;
    this.form = this.emptyForm();
    this.successMessage = '';
    this.showForm = true;
  }

  edit(product: Product): void {
    this.errorMessage = '';
    this.productApi.getById(product.id).subscribe({
      next: selected => {
        this.editingProductId = selected.id;
        this.form = {
          categoryId: selected.categoryId,
          sku: selected.sku,
          name: selected.name,
          description: selected.description ?? '',
          price: selected.price,
          initialStock: 0,
          minimumStock: selected.minimumStock,
          isActive: selected.isActive
        };
        this.showForm = true;
      },
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudo cargar el producto.')
    });
  }

  save(): void {
    if (this.isSaving) {
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';
    const request = this.editingProductId === null
      ? this.productApi.create(this.createRequest())
      : this.productApi.update(this.editingProductId, this.updateRequest());

    request.subscribe({
      next: () => {
        this.successMessage = this.editingProductId === null
          ? 'Producto creado.'
          : 'Producto actualizado. El stock se modifica desde Inventario.';
        this.isSaving = false;
        this.showForm = false;
        this.loadProducts();
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo guardar el producto.');
        this.isSaving = false;
      }
    });
  }

  delete(product: Product): void {
    if (!window.confirm(`¿Eliminar el producto "${product.name}"?`)) {
      return;
    }

    this.errorMessage = '';
    this.productApi.delete(product.id).subscribe({
      next: () => {
        this.successMessage = 'Producto eliminado o desactivado.';
        this.loadProducts();
      },
      error: error => this.errorMessage = getApiErrorMessage(error, 'No se pudo eliminar el producto.')
    });
  }

  cancelForm(): void {
    this.showForm = false;
    this.errorMessage = '';
  }

  private createRequest(): CreateProductRequest {
    return {
      categoryId: this.form.categoryId ?? 0,
      sku: this.form.sku,
      name: this.form.name,
      description: this.form.description || undefined,
      price: this.form.price,
      initialStock: this.form.initialStock,
      minimumStock: this.form.minimumStock
    };
  }

  private updateRequest(): UpdateProductRequest {
    return {
      categoryId: this.form.categoryId ?? 0,
      sku: this.form.sku,
      name: this.form.name,
      description: this.form.description || undefined,
      price: this.form.price,
      minimumStock: this.form.minimumStock,
      isActive: this.form.isActive
    };
  }

  private emptyForm(): ProductFormState {
    return {
      categoryId: null,
      sku: '',
      name: '',
      description: '',
      price: 0,
      initialStock: 0,
      minimumStock: 5,
      isActive: true
    };
  }
}
