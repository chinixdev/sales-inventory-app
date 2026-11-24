import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { getApiErrorMessage } from '../../../core/models/api-response.model';
import { SystemHealth } from '../../../core/models/system-health.model';
import { AuthService } from '../../../core/services/auth.service';
import { SystemHealthApiService } from '../../../core/services/system-health-api.service';
import { StateMessageComponent } from '../../../shared/components/state-message/state-message.component';
import { AppDatePipe } from '../../../shared/pipes/app-date.pipe';
import { Bar, buildBars, formatDuration } from './system-health.utils';

interface ViewError {
  status: number;
  message: string;
}

const HTTP_MESSAGES: Record<number, string> = {
  401: 'Tu sesión no es válida o expiró. Inicia sesión nuevamente.',
  403: 'Solo los administradores pueden consultar el estado del sistema.'
};

const LABELS: Record<string, string> = {
  Healthy: 'Saludable',
  Degraded: 'Degradado',
  Unknown: 'Desconocido',
  Active: 'Activo',
  Inactive: 'Inactivo',
  NoInformation: 'Sin información',
  Up: 'En línea',
  Available: 'Disponible',
  Unavailable: 'No disponible'
};

const WARNINGS: Record<string, string> = {
  sql_unavailable: 'SQL Server no respondió: no hay métricas disponibles.',
  worker_unavailable: 'No se pudo consultar el estado del Worker.',
  worker_no_information: 'No hay información de ningún Worker: no se puede confirmar que se procesen eventos.',
  worker_inactive: 'Ninguna instancia del Worker ha enviado latido recientemente.',
  worker_processing_degraded: 'El Worker late, pero su último ciclo de procesamiento falló o está retrasado.',
  events_unavailable: 'No se pudo consultar el estado de los eventos.',
  outbox_backlog_old: 'Hay eventos pendientes con una antigüedad superior al umbral.',
  outbox_expired_leases: 'Hay eventos en procesamiento con el lease vencido.',
  outbox_failures_recent: 'Hubo eventos fallidos en las últimas 24 horas.'
};

export const CHART_WIDTH = 720;
export const CHART_HEIGHT = 140;

@Component({
  selector: 'app-system-health',
  standalone: true,
  imports: [StateMessageComponent, AppDatePipe],
  templateUrl: './system-health.component.html',
  styleUrls: ['./system-health.component.scss']
})
export class SystemHealthComponent implements OnInit, OnDestroy {
  private readonly api = inject(SystemHealthApiService);
  private readonly auth = inject(AuthService);

  /** Intervalo de la actualización automática (configurable en environment.systemHealthPollingMs). */
  protected readonly pollingMs = Math.max(environment.systemHealthPollingMs ?? 30000, 5000);
  protected readonly pollingSeconds = Math.round(this.pollingMs / 1000);
  protected readonly chartWidth = CHART_WIDTH;
  protected readonly chartHeight = CHART_HEIGHT;

  /** Defensa adicional: la ruta ya exige Admin y la API rechaza a cualquier otro rol. */
  protected readonly isAdmin = computed(() => this.auth.hasRole('Admin'));

  protected readonly health = signal<SystemHealth | null>(null);
  protected readonly isLoading = signal(false);
  protected readonly error = signal<ViewError | null>(null);
  protected readonly lastSuccessAt = signal<string | null>(null);
  protected readonly autoRefresh = signal(true);

  protected readonly bars = computed<Bar[]>(() => {
    const series = this.health()?.series;
    return series ? buildBars(series.points, CHART_WIDTH, CHART_HEIGHT) : [];
  });
  protected readonly canRetry = computed(() => {
    const status = this.error()?.status;
    return status !== 401 && status !== 403;
  });

  private timer: ReturnType<typeof setInterval> | null = null;
  private request: Subscription | null = null;

  constructor() {
    // Al cerrar sesión (el token desaparece) se corta el sondeo y se descarta lo mostrado.
    effect(() => {
      if (!this.auth.currentUser()) {
        this.stopPolling();
        this.health.set(null);
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
    this.startPolling();
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  protected get isPolling(): boolean {
    return this.timer !== null;
  }

  protected toggleAutoRefresh(enabled: boolean): void {
    this.autoRefresh.set(enabled);
    if (enabled) {
      this.startPolling();
    } else {
      this.stopPolling();
    }
  }

  /** Actualización manual o automática. Una consulta nueva descarta la respuesta de la anterior (obsoleta). */
  protected refresh(): void {
    if (!this.auth.isAuthenticated()) {
      this.stopPolling();
      return;
    }

    this.request?.unsubscribe();
    this.isLoading.set(true);
    this.request = this.api.get().subscribe({
      next: health => {
        this.health.set(health);
        this.error.set(null);
        this.lastSuccessAt.set(new Date().toISOString());
        this.isLoading.set(false);
      },
      error: error => {
        const status = typeof error?.status === 'number' ? error.status : 0;
        this.error.set({ status, message: HTTP_MESSAGES[status] ?? getApiErrorMessage(error, 'No se pudo consultar el estado del sistema.') });
        this.isLoading.set(false);
        if (status === 401 || status === 403) {
          // Sin permisos no se sigue consultando ni se conserva lo mostrado.
          this.health.set(null);
          this.stopPolling();
        }
      }
    });
  }

  private startPolling(): void {
    if (this.timer !== null || !this.autoRefresh()) {
      return;
    }

    this.timer = setInterval(() => {
      // Sin solicitudes superpuestas: si la anterior sigue en curso, este ciclo se omite.
      if (!this.isLoading()) {
        this.refresh();
      }
    }, this.pollingMs);
  }

  private stopPolling(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    this.request?.unsubscribe();
    this.request = null;
    this.isLoading.set(false);
  }

  protected label(value: string | null | undefined): string {
    return value ? (LABELS[value] ?? value) : 'Desconocido';
  }

  protected tone(value: string | null | undefined): string {
    switch (value) {
      case 'Healthy':
      case 'Active':
      case 'Up':
      case 'Available':
        return 'tone-ok';
      case 'Degraded':
      case 'Inactive':
      case 'Unavailable':
        return 'tone-bad';
      default:
        return 'tone-muted'; // Unknown, NoInformation
    }
  }

  protected warning(code: string): string {
    return WARNINGS[code] ?? code;
  }

  protected duration(seconds: number | null | undefined): string | null {
    return formatDuration(seconds);
  }
}
