import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedResult } from '../models/paged-result.model';
import { ApiQueryParams, ApiService } from './api.service';
import { StockMovement } from '../../features/inventory/models/stock-movement.model';

export interface MovementQuery extends ApiQueryParams {
  productId?: number;
  pageNumber?: number;
  pageSize?: number;
}

export interface StockEntryRequest {
  productId: number;
  quantity: number;
  reason: string;
}

export interface StockAdjustmentRequest {
  productId: number;
  newStock: number;
  reason: string;
}

@Injectable({ providedIn: 'root' })
export class InventoryApiService {
  private readonly api = inject(ApiService);

  getMovements(query: MovementQuery = {}): Observable<PagedResult<StockMovement>> {
    return this.api.get<PagedResult<StockMovement>>('inventory/movements', query);
  }

  registerEntry(request: StockEntryRequest): Observable<StockMovement> {
    return this.api.post<StockMovement>('inventory/entries', request);
  }

  registerAdjustment(request: StockAdjustmentRequest): Observable<StockMovement> {
    return this.api.post<StockMovement>('inventory/adjustments', request);
  }
}
