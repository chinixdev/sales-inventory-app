import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedResult } from '../models/paged-result.model';
import { OutboxMessageDetail, OutboxMessageItem, OutboxSummary } from '../models/outbox.model';
import { ApiQueryParams, ApiService } from './api.service';

export interface OutboxQuery extends ApiQueryParams {
  pageNumber?: number;
  pageSize?: number;
  status?: string;
  eventType?: string;
  from?: string;
  to?: string;
}

/** Monitor de eventos del Outbox (endpoints exclusivos de Admin). */
@Injectable({ providedIn: 'root' })
export class OutboxApiService {
  private readonly api = inject(ApiService);

  getSummary(): Observable<OutboxSummary> {
    return this.api.get<OutboxSummary>('outbox/summary');
  }

  getMessages(query: OutboxQuery = {}): Observable<PagedResult<OutboxMessageItem>> {
    return this.api.get<PagedResult<OutboxMessageItem>>('outbox/messages', query);
  }

  getMessage(id: string): Observable<OutboxMessageDetail> {
    return this.api.get<OutboxMessageDetail>(`outbox/messages/${id}`);
  }

  /** Reprocesamiento manual: el backend solo acepta eventos Failed (409 en cualquier otro estado). */
  requeue(id: string): Observable<void> {
    return this.api.post<void>(`outbox/messages/${id}/requeue`, {});
  }
}
