import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { AuditLogDetail, AuditLogItem } from '../models/audit-log.model';
import { PagedResult } from '../models/paged-result.model';
import { ApiQueryParams, ApiService } from './api.service';

export interface AuditLogQuery extends ApiQueryParams {
  pageNumber?: number;
  pageSize?: number;
  entityName?: string;
  action?: string;
  userId?: number;
  from?: string;
  to?: string;
  correlationId?: string;
}

/** Consulta de auditoría (solo lectura, exclusiva de Admin): no existen métodos de escritura. */
@Injectable({ providedIn: 'root' })
export class AuditLogApiService {
  private readonly api = inject(ApiService);

  getAll(query: AuditLogQuery = {}): Observable<PagedResult<AuditLogItem>> {
    return this.api.get<PagedResult<AuditLogItem>>('audit-logs', query);
  }

  getById(id: number): Observable<AuditLogDetail> {
    return this.api.get<AuditLogDetail>(`audit-logs/${id}`);
  }
}
