import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { PagedResult } from '../models/paged-result.model';
import { AppNotification, MarkedRead, UnreadCount } from '../models/notification.model';
import { ApiQueryParams, ApiService } from './api.service';

export interface NotificationQuery extends ApiQueryParams {
  pageNumber?: number;
  pageSize?: number;
  /** true: solo no leídas; false: solo leídas; sin valor: todas. */
  unread?: boolean;
  type?: string;
}

@Injectable({ providedIn: 'root' })
export class NotificationApiService {
  private readonly api = inject(ApiService);

  getPaged(query: NotificationQuery = {}): Observable<PagedResult<AppNotification>> {
    return this.api.get<PagedResult<AppNotification>>('notifications', query);
  }

  getUnreadCount(): Observable<UnreadCount> {
    return this.api.get<UnreadCount>('notifications/unread-count');
  }

  markAsRead(id: number): Observable<void> {
    return this.api.post<void>(`notifications/${id}/read`, {});
  }

  markAllAsRead(): Observable<MarkedRead> {
    return this.api.post<MarkedRead>('notifications/read-all', {});
  }
}
