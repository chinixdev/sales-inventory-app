import { ScrollRegionDirective } from '../../shared/directives/scroll-region.directive';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { AppDatePipe } from '../../shared/pipes/app-date.pipe';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin, map, of, switchMap } from 'rxjs';
import { IconComponent } from '../../shared/components/icon/icon.component';
import { StateMessageComponent } from '../../shared/components/state-message/state-message.component';
import { PenPipe, formatPen } from '../../shared/pipes/pen.pipe';
import { ProductApiService } from '../../core/services/product-api.service';
import { SalesApiService } from '../../core/services/sales-api.service';
import { HealthApiService } from '../../core/services/health-api.service';
import { getApiErrorMessage } from '../../core/models/api-response.model';
import { Product } from '../products/models/product.model';
import { Sale } from '../sales/models/sale.model';

const PAGE_SIZE = 100;
/** Máximo de páginas que se consolidan; más allá los totales no se consideran verificables. */
const MAX_SALE_PAGES = 10;
const RECENT_COUNT = 5;
const DAYS = 7;

/** Estado asíncrono de una sección, basado en signals (compatible con zoneless). */
class Section<T> {
  readonly data = signal<T | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');

  load(source: Observable<T>, fallback: string): void {
    this.loading.set(true);
    this.error.set('');
    source.subscribe({
      next: value => {
        this.data.set(value);
        this.loading.set(false);
      },
      error: error => {
        this.error.set(getApiErrorMessage(error, fallback));
        this.loading.set(false);
      }
    });
  }
}

interface WeekSales {
  /** false si hay más ventas de las que se pueden consolidar: no se muestran importes. */
  complete: boolean;
  totalSalesInRange: number;
  days: DayBucket[];
}

interface DayBucket {
  key: string;
  label: string;
  count: number;
  amount: number;
}

interface StockAlerts {
  totalLow: number;
  items: Product[];
  complete: boolean;
}

/** Clave de día en hora local del navegador (YYYY-MM-DD). */
function dayKey(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

/** Hora UTC (sin sufijo Z) del instante dado: el backend guarda SaleDate en UTC y evita conversiones de zona. */
function toUtcWallClock(date: Date): string {
  return date.toISOString().slice(0, 23);
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [ScrollRegionDirective, AppDatePipe, RouterLink, IconComponent, StateMessageComponent, PenPipe],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
  private readonly productApi = inject(ProductApiService);
  private readonly salesApi = inject(SalesApiService);
  private readonly healthApi = inject(HealthApiService);

  protected readonly week = new Section<WeekSales>();
  protected readonly recent = new Section<Sale[]>();
  protected readonly activeProducts = new Section<number>();
  protected readonly alerts = new Section<StockAlerts>();
  protected readonly apiStatus = signal('Comprobando API…');
  protected readonly apiOk = signal(false);

  private readonly todayKey = dayKey(new Date());

  protected readonly today = computed(() => {
    const data = this.week.data();
    return data?.days.find(day => day.key === this.todayKey) ?? null;
  });

  protected readonly outOfStock = computed(() => {
    const data = this.alerts.data();
    return data?.complete ? data.items.filter(product => product.stock <= 0).length : null;
  });

  protected readonly maxAmount = computed(() =>
    Math.max(0, ...(this.week.data()?.days.map(day => day.amount) ?? []))
  );

  protected readonly weekTotal = computed(() =>
    this.week.data()?.days.reduce((sum, day) => sum + day.amount, 0) ?? 0
  );

  protected readonly chartLabel = computed(() => {
    const data = this.week.data();
    return data?.complete
      ? 'Ventas por día, últimos 7 días: ' + data.days.map(day => `${day.label} ${formatPen(day.amount)}`).join('; ')
      : '';
  });

  /** Alertas ordenadas con las agotadas primero (solo dentro de lo cargado). */
  protected readonly sortedAlerts = computed(() =>
    [...(this.alerts.data()?.items ?? [])].sort((a, b) => a.stock - b.stock || a.name.localeCompare(b.name))
  );

  protected barHeight(amount: number): number {
    const max = this.maxAmount();
    return max > 0 ? Math.max(2, (amount / max) * 100) : 0;
  }

  ngOnInit(): void {
    this.week.load(this.loadWeekSales(), 'No se pudieron cargar las ventas de la semana.');
    this.recent.load(
      this.salesApi.getPaged({ pageNumber: 1, pageSize: RECENT_COUNT }).pipe(map(result => result.items)),
      'No se pudieron cargar las ventas recientes.'
    );
    this.activeProducts.load(
      this.productApi.getPaged({ pageNumber: 1, pageSize: 1, activeOnly: true }).pipe(map(result => result.totalItems)),
      'No se pudo contar los productos activos.'
    );
    this.alerts.load(
      this.productApi.getPaged({ pageNumber: 1, pageSize: PAGE_SIZE, activeOnly: true, lowStockOnly: true }).pipe(
        map(result => ({
          totalLow: result.totalItems,
          items: result.items,
          complete: result.totalItems <= result.items.length
        }))
      ),
      'No se pudieron cargar las alertas de stock.'
    );

    this.healthApi.getStatus().subscribe({
      next: health => {
        this.apiOk.set(health.status === 'Healthy');
        this.apiStatus.set(`${health.service}: ${health.status}`);
      },
      error: () => {
        this.apiOk.set(false);
        this.apiStatus.set('API no disponible');
      }
    });
  }

  /**
   * Consolida las ventas de los últimos 7 días recorriendo todas las páginas.
   * Si superan el máximo consolidable, se marca como incompleto para no presentar parciales como totales.
   */
  private loadWeekSales(): Observable<WeekSales> {
    const buckets = this.emptyBuckets();
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    start.setDate(start.getDate() - (DAYS - 1));
    const from = toUtcWallClock(start);
    const query = (pageNumber: number) =>
      this.salesApi.getPaged({ pageNumber, pageSize: PAGE_SIZE, from });

    return query(1).pipe(
      switchMap(first => {
        if (first.totalPages > MAX_SALE_PAGES) {
          return of({ complete: false, totalSalesInRange: first.totalItems, days: buckets });
        }

        const rest = Array.from({ length: Math.max(0, first.totalPages - 1) }, (_, i) => query(i + 2));
        return (rest.length ? forkJoin(rest) : of([])).pipe(
          map(pages => {
            const sales = [first, ...pages].flatMap(page => page.items);
            for (const sale of sales) {
              const bucket = buckets.find(day => day.key === dayKey(new Date(this.asUtc(sale.saleDate))));
              if (bucket) {
                bucket.count += 1;
                bucket.amount += sale.total;
              }
            }

            buckets.forEach(day => (day.amount = Math.round(day.amount * 100) / 100));
            return { complete: true, totalSalesInRange: first.totalItems, days: buckets };
          })
        );
      })
    );
  }

  /** SaleDate llega en UTC; si no trae zona, se interpreta explícitamente como UTC. */
  private asUtc(value: string): string {
    return /[zZ]|[+-]\d{2}:?\d{2}$/.test(value) ? value : `${value}Z`;
  }

  private emptyBuckets(): DayBucket[] {
    const formatter = new Intl.DateTimeFormat('es-PE', { weekday: 'short', day: '2-digit' });
    return Array.from({ length: DAYS }, (_, index) => {
      const date = new Date();
      date.setHours(12, 0, 0, 0);
      date.setDate(date.getDate() - (DAYS - 1 - index));
      return { key: dayKey(date), label: formatter.format(date), count: 0, amount: 0 };
    });
  }
}
