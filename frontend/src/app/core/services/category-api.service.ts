import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiService } from './api.service';
import { Category } from '../../features/categories/models/category.model';

@Injectable({ providedIn: 'root' })
export class CategoryApiService {
  private readonly api = inject(ApiService);

  getAll(activeOnly?: boolean): Observable<Category[]> {
    return this.api.get<Category[]>('categories', activeOnly === undefined ? undefined : { activeOnly });
  }

  getById(id: number): Observable<Category> {
    return this.api.get<Category>(`categories/${id}`);
  }

  create(request: { name: string }): Observable<Category> {
    return this.api.post<Category>('categories', request);
  }

  update(id: number, request: { name: string; isActive: boolean }): Observable<Category> {
    return this.api.put<Category>(`categories/${id}`, request);
  }

  delete(id: number): Observable<void> {
    return this.api.delete<void>(`categories/${id}`);
  }
}
