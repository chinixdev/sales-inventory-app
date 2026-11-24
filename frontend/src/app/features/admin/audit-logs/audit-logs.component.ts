import { Component, ElementRef, HostListener, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { getApiErrorMessage } from '../../../core/models/api-response.model';
import { AUDIT_ACTIONS, AUDIT_ENTITIES, AuditLogDetail, AuditLogItem } from '../../../core/models/audit-log.model';
import { AuditLogApiService } from '../../../core/services/audit-log-api.service';
import { AuthService } from '../../../core/services/auth.service';
import { StateMessageComponent } from '../../../shared/components/state-message/state-message.component';
import { ScrollRegionDirective } from '../../../shared/directives/scroll-region.directive';
import { AppDatePipe } from '../../../shared/pipes/app-date.pipe';
import { ValueRow, buildValueRows } from './audit-values';

/** Hora UTC (sin «Z») del instante local indicado, igual que el resto de filtros de fecha de la aplicación. */
function toUtcWallClock(localDateTime: string): string {
  return new Date(localDateTime).toISOString().slice(0, 23);
}

interface ViewError {
  status: number;
  message: string;
}

const HTTP_MESSAGES: Record<number, string> = {
  401: 'Tu sesión no es válida o expiró. Inicia sesión nuevamente.',
  403: 'Solo los administradores pueden consultar la auditoría.',
  404: 'El registro de auditoría solicitado no existe.'
};

function describeError(error: unknown, fallback: string): ViewError {
  const status = typeof error === 'object' && error !== null && 'status' in error && typeof error.status === 'number' ? error.status : 0;
  return { status, message: HTTP_MESSAGES[status] ?? getApiErrorMessage(error, fallback) };
}

@Component({
  selector: 'app-audit-logs',
  standalone: true,
  imports: [FormsModule, StateMessageComponent, ScrollRegionDirective, AppDatePipe],
  templateUrl: './audit-logs.component.html',
  styleUrls: ['./audit-logs.component.scss']
})
export class AuditLogsComponent implements OnInit, OnDestroy {
  private readonly api = inject(AuditLogApiService);
  private readonly auth = inject(AuthService);

  protected readonly entities = AUDIT_ENTITIES;
  protected readonly actions = AUDIT_ACTIONS;

  /** Defensa adicional: la ruta ya exige Admin y la API rechaza a cualquier otro rol. */
  protected readonly isAdmin = computed(() => this.auth.hasRole('Admin'));

  // Listado
  protected readonly logs = signal<AuditLogItem[]>([]);
  protected readonly isLoading = signal(false);
  protected readonly loadError = signal<ViewError | null>(null);
  protected readonly pageNumber = signal(1);
  protected readonly totalItems = signal(0);
  protected readonly totalPages = signal(0);
  protected readonly pageSize = 20;

  // Filtros
  protected readonly entityFilter = signal('');
  protected readonly actionFilter = signal('');
  protected readonly userFilter = signal('');
  protected readonly fromDate = signal('');
  protected readonly toDate = signal('');
  protected readonly correlationFilter = signal('');
  protected readonly filterError = signal('');
  protected readonly hasFilters = computed(
    () => !!(this.entityFilter() || this.actionFilter() || this.userFilter().trim() || this.fromDate() || this.toDate() || this.correlationFilter().trim())
  );

  // Detalle (panel lateral modal)
  protected readonly detailOpen = signal(false);
  protected readonly detailLoading = signal(false);
  protected readonly detailError = signal<ViewError | null>(null);
  protected readonly detail = signal<AuditLogDetail | null>(null);
  protected readonly valueRows = computed<ValueRow[]>(() => {
    const d = this.detail();
    return d ? buildValueRows(d.oldValues, d.newValues, d.changedFields ?? [], d.action) : [];
  });
  protected readonly copyNotice = signal('');

  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  private listRequest?: Subscription;
  private detailRequest?: Subscription;
  private opener: HTMLElement | null = null;

  ngOnInit(): void {
    this.loadLogs();
  }

  ngOnDestroy(): void {
    this.listRequest?.unsubscribe();
    this.detailRequest?.unsubscribe();
  }

  protected loadLogs(): void {
    // Una respuesta tardía de una consulta anterior nunca debe pisar a la más reciente.
    this.listRequest?.unsubscribe();
    this.isLoading.set(true);
    this.loadError.set(null);
    const user = this.userFilter().trim();
    this.listRequest = this.api.getAll({
      pageNumber: this.pageNumber(),
      pageSize: this.pageSize,
      entityName: this.entityFilter() || undefined,
      action: this.actionFilter() || undefined,
      userId: user ? Number(user) : undefined,
      from: this.fromDate() ? toUtcWallClock(`${this.fromDate()}T00:00:00`) : undefined,
      to: this.toDate() ? toUtcWallClock(`${this.toDate()}T23:59:59.999`) : undefined,
      correlationId: this.correlationFilter().trim() || undefined
    }).subscribe({
      next: result => {
        this.logs.set(result.items);
        this.totalItems.set(result.totalItems);
        this.totalPages.set(result.totalPages);
        this.isLoading.set(false);
      },
      error: error => {
        this.logs.set([]);
        this.loadError.set(describeError(error, 'No se pudo cargar la auditoría.'));
        this.isLoading.set(false);
      }
    });
  }

  protected applyFilters(): void {
    const user = this.userFilter().trim();
    if (user && !/^[1-9]\d{0,9}$/.test(user)) {
      this.filterError.set('El usuario debe ser un identificador numérico positivo.');
      return;
    }

    if (this.fromDate() && this.toDate() && this.fromDate() > this.toDate()) {
      this.filterError.set('La fecha «Desde» no puede ser posterior a «Hasta».');
      return;
    }

    this.filterError.set('');
    this.pageNumber.set(1);
    this.loadLogs();
  }

  protected clearFilters(): void {
    this.entityFilter.set('');
    this.actionFilter.set('');
    this.userFilter.set('');
    this.fromDate.set('');
    this.toDate.set('');
    this.correlationFilter.set('');
    this.applyFilters();
  }

  protected changePage(page: number): void {
    if (page >= 1 && page <= this.totalPages()) {
      this.pageNumber.set(page);
      this.loadLogs();
    }
  }

  protected actor(log: Pick<AuditLogItem, 'actorType' | 'userId'>): string {
    return log.actorType === 'User' && log.userId !== null ? `Usuario #${log.userId}` : 'Sistema';
  }

  protected actionClass(action: string): string {
    return `action-${String(action).toLowerCase()}`;
  }

  // ─── Detalle ───

  protected openDetail(id: number, event?: Event): void {
    this.opener = event?.currentTarget instanceof HTMLElement ? event.currentTarget : (document.activeElement as HTMLElement | null);
    this.detailRequest?.unsubscribe();
    this.detail.set(null);
    this.detailError.set(null);
    this.copyNotice.set('');
    this.detailOpen.set(true);
    this.detailLoading.set(true);
    setTimeout(() => this.panel()?.nativeElement.focus());

    this.detailRequest = this.api.getById(id).subscribe({
      next: detail => {
        this.detail.set(detail);
        this.detailLoading.set(false);
      },
      error: error => {
        this.detailError.set(describeError(error, 'No se pudo cargar el detalle del registro.'));
        this.detailLoading.set(false);
      }
    });
  }

  @HostListener('document:keydown.escape')
  protected closeDetail(): void {
    if (!this.detailOpen()) {
      return;
    }

    this.detailRequest?.unsubscribe();
    this.detailOpen.set(false);
    const target = this.opener;
    this.opener = null;
    setTimeout(() => target?.isConnected && target.focus());
  }

  protected trapFocus(event: KeyboardEvent): void {
    if (event.key !== 'Tab') {
      return;
    }

    const panel = this.panel()?.nativeElement;
    const focusable = Array.from(panel?.querySelectorAll<HTMLElement>('button:not([disabled])') ?? []);
    if (!panel || focusable.length === 0) {
      event.preventDefault();
      panel?.focus();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  protected async copyCorrelation(value: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(value);
      this.copyNotice.set('CorrelationId copiado al portapapeles.');
    } catch {
      this.copyNotice.set('No se pudo copiar automáticamente; selecciona el texto y cópialo manualmente.');
    }
  }

  /** Atajo desde el detalle: lista todos los registros de la misma operación. */
  protected filterByCorrelation(value: string): void {
    this.correlationFilter.set(value);
    this.closeDetail();
    this.applyFilters();
  }
}
