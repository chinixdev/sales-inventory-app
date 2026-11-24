import { ScrollRegionDirective } from '../../../../shared/directives/scroll-region.directive';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Category } from '../../models/category.model';
import { CategoryApiService } from '../../../../core/services/category-api.service';
import { AuthService } from '../../../../core/services/auth.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';
import { StateMessageComponent } from '../../../../shared/components/state-message/state-message.component';
import { ConfirmDialogComponent } from '../../../../shared/components/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-category-list',
  standalone: true,
  imports: [ScrollRegionDirective, FormsModule, StateMessageComponent, ConfirmDialogComponent],
  templateUrl: './category-list.component.html',
  styleUrls: ['./category-list.component.scss']
})
export class CategoryListComponent implements OnInit {
  private readonly categoryApi = inject(CategoryApiService);
  private readonly auth = inject(AuthService);

  /** Solo Admin ve y ejecuta escrituras; la API lo vuelve a validar. */
  protected readonly canWrite = computed(() => this.auth.hasRole('Admin'));

  protected readonly categories = signal<Category[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly isSaving = signal(false);
  protected readonly showForm = signal(false);
  protected readonly editingCategoryId = signal<number | null>(null);
  protected readonly pendingDelete = signal<Category | null>(null);
  protected readonly loadError = signal('');
  protected readonly actionError = signal('');
  protected readonly successMessage = signal('');

  protected name = '';
  protected isActive = true;

  ngOnInit(): void {
    this.loadCategories();
  }

  protected loadCategories(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    this.categoryApi.getAll().subscribe({
      next: categories => {
        this.categories.set(categories);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar las categorías.'));
        this.isLoading.set(false);
      }
    });
  }

  protected startCreate(): void {
    if (!this.canWrite()) {
      return;
    }

    this.editingCategoryId.set(null);
    this.name = '';
    this.isActive = true;
    this.successMessage.set('');
    this.actionError.set('');
    this.showForm.set(true);
  }

  protected edit(category: Category): void {
    if (!this.canWrite()) {
      return;
    }

    this.actionError.set('');
    this.successMessage.set('');
    this.categoryApi.getById(category.id).subscribe({
      next: selected => {
        this.editingCategoryId.set(selected.id);
        this.name = selected.name;
        this.isActive = selected.isActive;
        this.showForm.set(true);
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudo cargar la categoría.'))
    });
  }

  protected save(): void {
    if (this.isSaving() || !this.canWrite()) {
      return;
    }

    const editingId = this.editingCategoryId();
    const name = this.name.trim();
    this.isSaving.set(true);
    this.actionError.set('');
    const request = editingId === null
      ? this.categoryApi.create({ name })
      : this.categoryApi.update(editingId, { name, isActive: this.isActive });

    request.subscribe({
      next: () => {
        this.successMessage.set(editingId === null
          ? 'Categoría creada correctamente.'
          : 'Categoría actualizada correctamente.');
        this.isSaving.set(false);
        this.showForm.set(false);
        this.loadCategories();
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo guardar la categoría.'));
        this.isSaving.set(false);
      }
    });
  }

  protected requestDelete(category: Category): void {
    if (this.canWrite()) {
      this.pendingDelete.set(category);
    }
  }

  protected confirmDelete(): void {
    const category = this.pendingDelete();
    this.pendingDelete.set(null);
    if (!category || !this.canWrite()) {
      return;
    }

    this.actionError.set('');
    this.successMessage.set('');
    this.categoryApi.delete(category.id).subscribe({
      next: () => {
        this.successMessage.set(`Categoría "${category.name}" eliminada.`);
        this.loadCategories();
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudo eliminar la categoría.'))
    });
  }

  protected cancelForm(): void {
    this.showForm.set(false);
    this.actionError.set('');
  }
}
