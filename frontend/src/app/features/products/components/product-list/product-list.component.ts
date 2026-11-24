import { ScrollRegionDirective } from '../../../../shared/directives/scroll-region.directive';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { PenPipe } from '../../../../shared/pipes/pen.pipe';
import { FormsModule } from '@angular/forms';
import { Product } from '../../models/product.model';
import { Category } from '../../../categories/models/category.model';
import { CategoryApiService } from '../../../../core/services/category-api.service';
import { CreateProductRequest, ProductApiService, UpdateProductRequest } from '../../../../core/services/product-api.service';
import { AuthService } from '../../../../core/services/auth.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';
import { StateMessageComponent } from '../../../../shared/components/state-message/state-message.component';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

interface ProductFormState {
  categoryId: number | null;
  sku: string;
  name: string;
  description: string;
  price: number | null;
  initialStock: number;
  minimumStock: number;
  isActive: boolean;
}

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [ScrollRegionDirective, PenPipe, FormsModule, StateMessageComponent, ConfirmDialogComponent],
  templateUrl: './product-list.component.html',
  styleUrls: ['./product-list.component.scss']
})
export class ProductListComponent implements OnInit {
  private readonly productApi = inject(ProductApiService);
  private readonly categoryApi = inject(CategoryApiService);
  private readonly auth = inject(AuthService);

  /** Solo Admin ve y ejecuta escrituras; la API lo vuelve a validar. */
  protected readonly canWrite = computed(() => this.auth.hasRole('Admin'));

  protected readonly products = signal<Product[]>([]);
  protected readonly categories = signal<Category[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly isSaving = signal(false);
  protected readonly loadError = signal('');
  protected readonly actionError = signal('');
  protected readonly successMessage = signal('');
  protected readonly showForm = signal(false);
  protected readonly editingProductId = signal<number | null>(null);
  protected readonly pendingDelete = signal<Product | null>(null);
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly hasActiveFilters = computed(() => this.filtersApplied());
  private readonly filtersApplied = signal(false);

  protected readonly pageSize = 10;
  protected searchTerm = '';
  protected categoryFilter: number | null = null;
  protected activeOnly: boolean | null = null;
  protected lowStockOnly = false;
  protected form: ProductFormState = this.emptyForm();

  ngOnInit(): void {
    this.loadCategories();
    this.loadProducts();
  }

  protected loadCategories(): void {
    this.categoryApi.getAll(true).subscribe({
      next: categories => this.categories.set(categories),
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudieron cargar las categorías.'))
    });
  }

  protected loadProducts(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    this.productApi.getPaged({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      searchTerm: this.searchTerm.trim() || undefined,
      categoryId: this.categoryFilter ?? undefined,
      activeOnly: this.activeOnly ?? undefined,
      lowStockOnly: this.lowStockOnly || undefined
    }).subscribe({
      next: result => {
        this.products.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar los productos.'));
        this.isLoading.set(false);
      }
    });
  }

  protected applyFilters(): void {
    this.filtersApplied.set(
      !!this.searchTerm.trim() || this.categoryFilter !== null || this.activeOnly !== null || this.lowStockOnly
    );
    this.pageNumber.set(1);
    this.loadProducts();
  }

  protected clearFilters(): void {
    this.searchTerm = '';
    this.categoryFilter = null;
    this.activeOnly = null;
    this.lowStockOnly = false;
    this.applyFilters();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.loadProducts();
    }
  }

  protected startCreate(): void {
    if (!this.canWrite()) {
      return;
    }

    this.editingProductId.set(null);
    this.form = this.emptyForm();
    this.successMessage.set('');
    this.actionError.set('');
    this.showForm.set(true);
  }

  protected edit(product: Product): void {
    if (!this.canWrite()) {
      return;
    }

    this.actionError.set('');
    this.successMessage.set('');
    this.productApi.getById(product.id).subscribe({
      next: selected => {
        this.editingProductId.set(selected.id);
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
        this.showForm.set(true);
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudo cargar el producto.'))
    });
  }

  protected save(): void {
    if (this.isSaving() || !this.canWrite()) {
      return;
    }

    const editingId = this.editingProductId();
    this.isSaving.set(true);
    this.actionError.set('');
    const request = editingId === null
      ? this.productApi.create(this.createRequest())
      : this.productApi.update(editingId, this.updateRequest());

    request.subscribe({
      next: () => {
        this.successMessage.set(editingId === null
          ? 'Producto creado correctamente.'
          : 'Producto actualizado. El stock se modifica desde Inventario.');
        this.isSaving.set(false);
        this.showForm.set(false);
        this.loadProducts();
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo guardar el producto.'));
        this.isSaving.set(false);
      }
    });
  }

  protected requestDelete(product: Product): void {
    if (this.canWrite()) {
      this.pendingDelete.set(product);
    }
  }

  protected confirmDelete(): void {
    const product = this.pendingDelete();
    this.pendingDelete.set(null);
    if (!product || !this.canWrite()) {
      return;
    }

    this.actionError.set('');
    this.successMessage.set('');
    this.productApi.delete(product.id).subscribe({
      next: () => {
        this.successMessage.set(`Producto "${product.name}" eliminado o desactivado.`);
        if (this.products().length === 1 && this.pageNumber() > 1) {
          this.pageNumber.update(page => page - 1);
        }
        this.loadProducts();
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudo eliminar el producto.'))
    });
  }

  protected cancelForm(): void {
    this.showForm.set(false);
    this.actionError.set('');
  }

  private createRequest(): CreateProductRequest {
    return {
      categoryId: this.form.categoryId ?? 0,
      sku: this.form.sku.trim(),
      name: this.form.name.trim(),
      description: this.form.description.trim() || undefined,
      price: this.form.price ?? 0,
      initialStock: this.form.initialStock,
      minimumStock: this.form.minimumStock
    };
  }

  private updateRequest(): UpdateProductRequest {
    const { initialStock: _ignored, ...rest } = this.createRequest();
    return { ...rest, isActive: this.form.isActive };
  }

  private emptyForm(): ProductFormState {
    return {
      categoryId: null,
      sku: '',
      name: '',
      description: '',
      price: null,
      initialStock: 0,
      minimumStock: 5,
      isActive: true
    };
  }
}
