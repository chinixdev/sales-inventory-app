import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedResult } from '../models/paged-result.model';
import { ApiQueryParams, ApiService } from './api.service';
import { Sale } from '../../features/sales/models/sale.model';

export interface SalesQuery extends ApiQueryParams {
  pageNumber?: number;
  pageSize?: number;
  userId?: number;
  from?: string;
  to?: string;
}

export interface CreateSaleRequest {
  items: Array<{ productId: number; quantity: number }>;
}

@Injectable({ providedIn: 'root' })
export class SalesApiService {
  private readonly api = inject(ApiService);

  getPaged(query: SalesQuery = {}): Observable<PagedResult<Sale>> {
    return this.api.get<PagedResult<Sale>>('sales', query);
  }

  getById(id: number): Observable<Sale> {
    return this.api.get<Sale>(`sales/${id}`);
  }

  /** El backend exige Idempotency-Key (UUID): la misma clave con el mismo contenido devuelve la venta original. */
  create(request: CreateSaleRequest, idempotencyKey: string): Observable<Sale> {
    return this.api.post<Sale>('sales', request, { 'Idempotency-Key': idempotencyKey });
  }
}
