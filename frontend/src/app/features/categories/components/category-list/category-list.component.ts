import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Category } from '../../models/category.model';
import { CategoryApiService } from '../../../../core/services/category-api.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';

@Component({
  selector: 'app-category-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './category-list.component.html',
  styleUrls: ['./category-list.component.scss']
})
export class CategoryListComponent implements OnInit {
  private readonly categoryApi = inject(CategoryApiService);

  categories: Category[] = [];
  isLoading = false;
  isSaving = false;
  showForm = false;
  editingCategoryId: number | null = null;
  name = '';
  isActive = true;
  errorMessage = '';
  successMessage = '';

  ngOnInit(): void {
    this.loadCategories();
  }

  loadCategories(): void {
    this.isLoading = true;
    this.errorMessage = '';
    this.categoryApi.getAll().subscribe({
      next: categories => {
        this.categories = categories;
        this.isLoading = false;
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar las categorías.');
        this.isLoading = false;
      }
    });
  }

  startCreate(): void {
    this.editingCategoryId = null;
    this.name = '';
    this.isActive = true;
    this.successMessage = '';
    this.showForm = true;
  }

  edit(category: Category): void {
    this.errorMessage = '';
    this.categoryApi.getById(category.id).subscribe({
      next: selected => {
        this.editingCategoryId = selected.id;
        this.name = selected.name;
        this.isActive = selected.isActive;
        this.showForm = true;
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo cargar la categoría.');
      }
    });
  }

  save(): void {
    if (this.isSaving) {
      return;
    }

    this.isSaving = true;
    this.errorMessage = '';
    const request = this.editingCategoryId === null
      ? this.categoryApi.create({ name: this.name })
      : this.categoryApi.update(this.editingCategoryId, { name: this.name, isActive: this.isActive });

    request.subscribe({
      next: () => {
        this.successMessage = this.editingCategoryId === null
          ? 'Categoría creada.'
          : 'Categoría actualizada.';
        this.isSaving = false;
        this.showForm = false;
        this.loadCategories();
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo guardar la categoría.');
        this.isSaving = false;
      }
    });
  }

  delete(category: Category): void {
    if (!window.confirm(`¿Eliminar la categoría "${category.name}"?`)) {
      return;
    }

    this.errorMessage = '';
    this.categoryApi.delete(category.id).subscribe({
      next: () => {
        this.successMessage = 'Categoría eliminada.';
        this.loadCategories();
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo eliminar la categoría.');
      }
    });
  }

  cancelForm(): void {
    this.showForm = false;
    this.errorMessage = '';
  }
}
