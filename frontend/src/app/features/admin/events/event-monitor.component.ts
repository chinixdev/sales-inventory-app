import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { getApiErrorMessage } from '../../../core/models/api-response.model';
import { OutboxMessageDetail, OutboxMessageItem, OutboxStatus, OutboxSummary } from '../../../core/models/outbox.model';
import { AuthService } from '../../../core/services/auth.service';
import { OutboxApiService } from '../../../core/services/outbox-api.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { StateMessageComponent } from '../../../shared/components/state-message/state-message.component';
import { ScrollRegionDirective } from '../../../shared/directives/scroll-region.directive';
import { AppDatePipe } from '../../../shared/pipes/app-date.pipe';

export type StatusFilter = '' | OutboxStatus;

/** Hora UTC (sin «Z») del instante local indicado, igual que el resto de filtros de fecha de la aplicación. */
function toUtcWallClock(localDateTime: string): string {
  return new Date(localDateTime).toISOString().slice(0, 23);
}

@Component({
  selector: 'app-event-monitor',
  standalone: true,
  imports: [FormsModule, StateMessageComponent, ConfirmDialogComponent, ScrollRegionDirective, AppDatePipe],
  templateUrl: './event-monitor.component.html',
  styleUrls: ['./event-monitor.component.scss']
})
export class EventMonitorComponent implements OnInit {
  private readonly api = inject(OutboxApiService);
  private readonly auth = inject(AuthService);

  /** Defensa adicional: la ruta ya exige Admin y la API rechaza a cualquier otro rol. */
  protected readonly isAdmin = computed(() => this.auth.hasRole('Admin'));

  // Resumen
  protected readonly summary = signal<OutboxSummary | null>(null);
  protected readonly summaryLoading = signal(true);
  protected readonly summaryError = signal('');

  // Listado
  protected readonly messages = signal<OutboxMessageItem[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly loadError = signal('');
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly pageSize = 15;

  // Filtros
  protected readonly statusFilter = signal<StatusFilter>('');
  protected readonly eventTypeFilter = signal('');
  protected readonly fromDate = signal('');
  protected readonly toDate = signal('');
  protected readonly hasFilters = computed(
    () => this.statusFilter() !== '' || this.eventTypeFilter().trim() !== '' || !!this.fromDate() || !!this.toDate()
  );

  // Detalle y reprocesamiento
  protected readonly selected = signal<OutboxMessageDetail | null>(null);
  protected readonly detailLoading = signal(false);
  protected readonly pendingRequeue = signal<{ id: string; eventType: string } | null>(null);
  protected readonly isRequeuing = signal(false);
  protected readonly actionError = signal('');
  protected readonly notice = signal('');

  ngOnInit(): void {
    this.refreshAll();
  }

  protected refreshAll(): void {
    this.loadSummary();
    this.loadMessages();
  }

  protected loadSummary(): void {
    this.summaryLoading.set(true);
    this.summaryError.set('');
    this.api.getSummary().subscribe({
      next: summary => {
        this.summary.set(summary);
        this.summaryLoading.set(false);
      },
      error: error => {
        this.summaryError.set(getApiErrorMessage(error, 'No se pudo cargar el resumen de eventos.'));
        this.summaryLoading.set(false);
      }
    });
  }

  protected loadMessages(): void {
    this.isLoading.set(true);
    this.loadError.set('');
    this.api.getMessages({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      status: this.statusFilter() || undefined,
      eventType: this.eventTypeFilter().trim() || undefined,
      from: this.fromDate() ? toUtcWallClock(`${this.fromDate()}T00:00:00`) : undefined,
      to: this.toDate() ? toUtcWallClock(`${this.toDate()}T23:59:59.999`) : undefined
    }).subscribe({
      next: result => {
        this.messages.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.loadError.set(getApiErrorMessage(error, 'No se pudieron cargar los eventos.'));
        this.isLoading.set(false);
      }
    });
  }

  protected filterByStatus(status: StatusFilter): void {
    this.statusFilter.set(status);
    this.applyFilters();
  }

  protected applyFilters(): void {
    this.pageNumber.set(1);
    this.loadMessages();
  }

  protected clearFilters(): void {
    this.statusFilter.set('');
    this.eventTypeFilter.set('');
    this.fromDate.set('');
    this.toDate.set('');
    this.applyFilters();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.loadMessages();
    }
  }

  protected showDetail(id: string): void {
    if (this.selected()?.id === id) {
      this.selected.set(null);
      return;
    }

    this.detailLoading.set(true);
    this.actionError.set('');
    this.api.getMessage(id).subscribe({
      next: detail => {
        this.selected.set(detail);
        this.detailLoading.set(false);
      },
      error: error => {
        this.actionError.set(getApiErrorMessage(error, 'No se pudo cargar el detalle del evento.'));
        this.detailLoading.set(false);
      }
    });
  }

  protected requestRequeue(message: { id: string; eventType: string; status: OutboxStatus }): void {
    // Solo los eventos Failed se pueden reprocesar; la API lo vuelve a validar de forma atómica.
    if (this.isAdmin() && message.status === 'Failed') {
      this.pendingRequeue.set({ id: message.id, eventType: message.eventType });
    }
  }

  protected confirmRequeue(): void {
    const target = this.pendingRequeue();
    this.pendingRequeue.set(null);
    if (!target || this.isRequeuing() || !this.isAdmin()) {
      return;
    }

    this.isRequeuing.set(true);
    this.actionError.set('');
    this.notice.set('');
    this.api.requeue(target.id).subscribe({
      next: () => {
        this.isRequeuing.set(false);
        this.notice.set(`El evento ${this.shortId(target.id)} volvió a la cola (Pending) para reprocesarse.`);
        this.selected.set(null);
        this.refreshAll();
      },
      error: error => {
        this.isRequeuing.set(false);
        this.actionError.set(getApiErrorMessage(error, 'No se pudo reprocesar el evento.'));
        // Un 409 indica que otro administrador ya lo reencoló o cambió de estado: se refresca la vista.
        this.refreshAll();
      }
    });
  }

  protected shortId(id: string): string {
    return id.slice(0, 8);
  }

  protected statusClass(status: OutboxStatus): string {
    return `status-${status.toLowerCase()}`;
  }
}
