import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AppNotification, NotificationType } from '../../core/models/notification.model';
import { getApiErrorMessage } from '../../core/models/api-response.model';
import { NotificationApiService } from '../../core/services/notification-api.service';
import { NotificationPollingService } from '../../core/services/notification-polling.service';
import { StateMessageComponent } from '../../shared/components/state-message/state-message.component';
import { AppDatePipe } from '../../shared/pipes/app-date.pipe';

export type ReadFilter = 'all' | 'unread' | 'read';

@Component({
  selector: 'app-notification-center',
  standalone: true,
  imports: [FormsModule, RouterLink, StateMessageComponent, AppDatePipe],
  templateUrl: './notification-center.component.html',
  styleUrls: ['./notification-center.component.scss']
})
export class NotificationCenterComponent implements OnInit {
  private readonly api = inject(NotificationApiService);
  private readonly polling = inject(NotificationPollingService);

  protected readonly items = signal<AppNotification[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly loadError = signal('');
  protected readonly actionError = signal('');
  protected readonly notice = signal('');
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly readFilter = signal<ReadFilter>('all');
  protected readonly typeFilter = signal<'' | NotificationType>('');
  protected readonly pageSize = 15;
  protected readonly hasFilters = computed(() => this.readFilter() !== 'all' || this.typeFilter() !== '');
  protected readonly hasUnreadOnPage = computed(() => this.items().some(item => !item.isRead));

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    const read = this.readFilter();
    this.api.getPaged({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      unread: read === 'all' ? undefined : read === 'unread',
      type: this.typeFilter() || undefined
    }).subscribe({
      next: result => {
        this.items.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar las notificaciones.'));
        this.isLoading.set(false);
      }
    });
  }

  protected applyFilters(): void {
    this.pageNumber.set(1);
    this.notice.set('');
    this.load();
  }

  protected clearFilters(): void {
    this.readFilter.set('all');
    this.typeFilter.set('');
    this.applyFilters();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.load();
    }
  }

  protected markAsRead(notification: AppNotification): void {
    if (notification.isRead) {
      return;
    }

    this.actionError.set('');
    this.api.markAsRead(notification.id).subscribe({
      next: () => {
        this.items.update(list =>
          list.map(item => item.id === notification.id ? { ...item, isRead: true } : item)
        );
        this.polling.refresh();
        // Con el filtro «no leídas» la notificación deja de pertenecer al listado.
        if (this.readFilter() === 'unread') {
          this.load();
        }
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudo marcar la notificación como leída.'))
    });
  }

  protected markAllAsRead(): void {
    this.actionError.set('');
    this.api.markAllAsRead().subscribe({
      next: result => {
        this.notice.set(result.updated > 0
          ? `${result.updated} notificación(es) marcada(s) como leída(s).`
          : 'No había notificaciones sin leer.');
        this.polling.setUnreadCount(0);
        this.load();
      },
      error: error => this.actionError.set(getApiErrorMessage(error, 'No se pudieron marcar las notificaciones.'))
    });
  }

  protected typeLabel(type: NotificationType): string {
    return type === 'OutOfStock' ? 'Agotado' : 'Stock bajo';
  }
}
