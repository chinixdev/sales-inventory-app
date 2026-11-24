import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedResult } from '../models/paged-result.model';
import { ApiQueryParams, ApiService } from './api.service';
import { Product } from '../../features/products/models/product.model';

export interface ProductQuery extends ApiQueryParams {
  pageNumber?: number;
  pageSize?: number;
  searchTerm?: string;
  categoryId?: number;
  activeOnly?: boolean;
  lowStockOnly?: boolean;
}

export interface CreateProductRequest {
  categoryId: number;
  sku: string;
  name: string;
  description?: string;
  price: number;
  initialStock: number;
  minimumStock: number;
}

export interface UpdateProductRequest extends Omit<CreateProductRequest, 'initialStock'> {
  isActive: boolean;
}

@Injectable({ providedIn: 'root' })
export class ProductApiService {
  private readonly api = inject(ApiService);

  getPaged(query: ProductQuery = {}): Observable<PagedResult<Product>> {
    return this.api.get<PagedResult<Product>>('products', query);
  }

  getById(id: number): Observable<Product> {
    return this.api.get<Product>(`products/${id}`);
  }

  create(request: CreateProductRequest): Observable<Product> {
    return this.api.post<Product>('products', request);
  }

  update(id: number, request: UpdateProductRequest): Observable<Product> {
    return this.api.put<Product>(`products/${id}`, request);
  }

  delete(id: number): Observable<void> {
    return this.api.delete<void>(`products/${id}`);
  }
}
