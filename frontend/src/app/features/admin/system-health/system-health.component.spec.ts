import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { roleGuard } from '../../../core/guards/auth.guard';
import { SystemHealth } from '../../../core/models/system-health.model';
import { AuthService } from '../../../core/services/auth.service';
import { SystemHealthComponent } from './system-health.component';
import { buildBars, formatDuration } from './system-health.utils';

function tokenFor(role: string): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  return `${encode({ alg: 'HS256' })}.${encode({ role, nameid: '1', exp: Math.floor(Date.now() / 1000) + 3600 })}.sig`;
}

const INSTANCE_ID = 'aaaaaaaa-1111-2222-3333-444444444444';

function healthy(overrides: Partial<SystemHealth> = {}): SystemHealth {
  return {
    generatedAtUtc: '2026-01-02T10:00:00Z',
    status: 'Healthy',
    api: { status: 'Up', version: '1.2.3' },
    sqlServer: { status: 'Available', latencyMs: 12 },
    worker: {
      status: 'Active',
      processing: 'Healthy',
      heartbeatThresholdSeconds: 45,
      instances: [{
        instanceId: INSTANCE_ID, version: '1.2.3', startedAtUtc: '2026-01-02T08:00:00Z', lastSeenAtUtc: '2026-01-02T09:59:55Z',
        secondsSinceLastSeen: 5, status: 'Active', processing: 'Healthy', lastSuccessfulCycleAtUtc: '2026-01-02T09:59:55Z', lastErrorType: null
      }]
    },
    events: {
      pending: 4, processing: 1, processed: 120, failed: 2, expiredLeases: 0, oldestPendingAtUtc: '2026-01-02T09:50:00Z',
      oldestPendingAgeSeconds: 603, processedLast24Hours: 90, failedLast24Hours: 1
    },
    seriesStatus: 'Available',
    series: {
      bucketMinutes: 60,
      points: [
        { bucketStartUtc: '2026-01-02T08:00:00Z', processed: 10, failed: 0 },
        { bucketStartUtc: '2026-01-02T09:00:00Z', processed: 30, failed: 2 }
      ]
    },
    warnings: [],
    ...overrides
  };
}

const unavailable: SystemHealth = {
  generatedAtUtc: null,
  status: 'Degraded',
  api: { status: 'Up', version: '1.2.3' },
  sqlServer: { status: 'Unavailable', latencyMs: null },
  worker: { status: 'Unavailable', processing: 'Unknown', heartbeatThresholdSeconds: 45, instances: null },
  events: null,
  seriesStatus: 'Unavailable',
  series: null,
  warnings: ['sql_unavailable']
};

function metricsOf(element: HTMLElement): string[] {
  return Array.from(element.querySelectorAll('.metrics > div')).map(
    d => `${d.querySelector('dt')?.textContent?.trim()} ${d.querySelector('dd')?.textContent?.replace(/\s+/g, ' ').trim()}`
  );
}

describe('SystemHealthComponent', () => {
  const isGet = (r: { method: string; url: string }) => r.method === 'GET' && r.url.endsWith('/admin/system-health');

  function setup(role = 'Admin', first: SystemHealth | 'skip' = healthy()) {
    vi.useFakeTimers();
    sessionStorage.setItem('salesinventory.accessToken', tokenFor(role));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(SystemHealthComponent);
    document.body.appendChild(fixture.nativeElement);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    if (first !== 'skip') {
      http.expectOne(isGet).flush(first);
      fixture.detectChanges();
    }
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => {
    vi.useRealTimers();
    document.body.replaceChildren();
    sessionStorage.clear();
  });

  describe('data', () => {
    it('shows API, SQL Server with latency, the Worker heartbeat and its processing state', () => {
      const { element } = setup();
      const text = element.textContent ?? '';
      expect(text).toContain('Versión 1.2.3');
      expect(text).toContain('Latencia: 12 ms');
      const row = element.querySelector('tbody tr') as HTMLElement;
      expect(row.textContent).toContain('aaaaaaaa');
      expect(row.textContent).toContain('hace 5 s');
      expect(row.textContent).toContain('Activo');
      expect(row.textContent).toContain('Saludable');
    });

    it('shows Outbox counts, the age of the oldest pending event and the 24 h totals', () => {
      const { element } = setup();
      const metrics = metricsOf(element);
      expect(metrics).toEqual([
        'Pendientes 4', 'En procesamiento 1', 'Procesados 120', 'Fallidos 2', 'Leases vencidos 0',
        'Pendiente más antiguo 10 min 3 s', 'Procesados (24 h) 90', 'Fallidos (24 h) 1'
      ]);
    });

    it('shows the date of the last successful query', () => {
      const { element } = setup();
      expect(element.querySelector('.last-success')?.textContent).toContain('Última consulta exitosa:');
    });
  });

  describe('status differentiation', () => {
    it('does not present a heartbeat as healthy processing: Active + Unknown stay distinct', () => {
      const data = healthy();
      data.worker.processing = 'Unknown';
      data.worker.instances![0].processing = 'Unknown';
      data.worker.instances![0].lastSuccessfulCycleAtUtc = null;
      const { element } = setup('Admin', data);

      const row = element.querySelector('tbody tr')!;
      expect(row.textContent).toContain('Activo');
      expect(row.textContent).toContain('Desconocido');
      expect(row.textContent).not.toContain('Saludable');
      expect(element.querySelector('tbody tr .tone-muted')).not.toBeNull();
    });

    it('shows Degraded processing with the last error type and a degraded banner with readable warnings', () => {
      const data = healthy({ status: 'Degraded', warnings: ['worker_processing_degraded', 'outbox_backlog_old', 'codigo_nuevo'] });
      data.worker.processing = 'Degraded';
      data.worker.instances![0].processing = 'Degraded';
      data.worker.instances![0].lastErrorType = 'SqlException';
      const { element } = setup('Admin', data);

      expect(element.querySelector('.overall.degraded')).not.toBeNull();
      const text = element.textContent ?? '';
      expect(text).toContain('Degradado');
      expect(text).toContain('SqlException');
      expect(text).toContain('su último ciclo de procesamiento falló');
      expect(text).toContain('antigüedad superior al umbral');
      expect(text).toContain('codigo_nuevo');
    });

    it('shows an inactive instance as Inactivo with unknown processing', () => {
      const data = healthy({ status: 'Degraded', warnings: ['worker_inactive'] });
      data.worker.status = 'Inactive';
      data.worker.processing = 'Unknown';
      Object.assign(data.worker.instances![0], { status: 'Inactive', processing: 'Unknown', secondsSinceLastSeen: 300 });
      const { element } = setup('Admin', data);

      const row = element.querySelector('tbody tr')!;
      expect(row.textContent).toContain('Inactivo');
      expect(row.textContent).toContain('hace 5 min');
    });

    it('shows NoInformation with the empty state (no instances is not the same as inactive)', () => {
      const data = healthy({ status: 'Degraded', warnings: ['worker_no_information'] });
      data.worker = { status: 'NoInformation', processing: 'Unknown', heartbeatThresholdSeconds: 45, instances: [] };
      const { element } = setup('Admin', data);

      const text = element.textContent ?? '';
      expect(text).toContain('Sin información');
      expect(text).toContain('Ningún Worker ha registrado latidos.');
      expect(text).toContain('no se puede afirmar que el Worker esté detenido');
      expect(element.querySelector('[aria-labelledby="workers-title"] tbody')).toBeNull();
    });
  });

  describe('null data is never turned into zeros', () => {
    it('shows the unavailable state when SQL Server did not respond', () => {
      const { element } = setup('Admin', unavailable);

      const text = element.textContent ?? '';
      expect(text).toContain('Latencia no disponible');
      expect(text).toContain('SQL Server no respondió');
      expect(text).toContain('No disponible: no se pudieron consultar los eventos.');
      expect(text).toContain('No disponible: no se pudo consultar la serie horaria.');
      expect(element.querySelector('.metrics')).toBeNull();
      expect(element.querySelector('svg')).toBeNull();
    });

    it('shows «No disponible» for a null 24 h total and «Sin pendientes» when nothing is pending', () => {
      const data = healthy();
      Object.assign(data.events!, { processedLast24Hours: null, failedLast24Hours: null, oldestPendingAgeSeconds: null, oldestPendingAtUtc: null, pending: 0 });
      const { element } = setup('Admin', data);

      const metrics = metricsOf(element);
      expect(metrics).toContain('Procesados (24 h) No disponible');
      expect(metrics).toContain('Fallidos (24 h) No disponible');
      expect(metrics).toContain('Pendiente más antiguo Sin pendientes');
      expect(metrics).not.toContain('Procesados (24 h) 0');
    });
  });

  describe('hourly chart', () => {
    it('draws one bar pair per persisted hour when the series is available, with an accessible table', () => {
      const { element } = setup();

      expect(element.querySelector('svg[role="img"]')).not.toBeNull();
      expect(element.querySelectorAll('rect.bar-processed')).toHaveLength(2);
      expect(element.querySelectorAll('details tbody tr')).toHaveLength(2);
    });

    it('explains insufficient data instead of drawing an empty chart', () => {
      const { element } = setup('Admin', healthy({ seriesStatus: 'InsufficientData', series: null }));

      expect(element.textContent).toContain('Aún no hay datos suficientes');
      expect(element.querySelector('svg')).toBeNull();
    });
  });

  describe('errors', () => {
    it('shows a generic error with retry when there is no data yet', () => {
      const { fixture, http, element, component } = setup('Admin', 'skip');
      http.expectOne(isGet).flush({}, { status: 500, statusText: 'Error' });
      fixture.detectChanges();

      expect(element.textContent).toContain('Reintentar');
      expect(component.health()).toBeNull();
    });

    it.each([[401, 'Tu sesión no es válida o expiró'], [403, 'Solo los administradores']])(
      'shows %i without retry, clears the data and stops polling', (status, text) => {
        const { fixture, http, element, component } = setup();
        component.refresh();
        http.expectOne(isGet).flush({}, { status, statusText: 'x' });
        fixture.detectChanges();

        expect(element.textContent).toContain(text);
        expect(element.textContent).not.toContain('Reintentar');
        expect(component.health()).toBeNull();
        expect(component.isPolling).toBe(false);

        vi.advanceTimersByTime(120_000);
        http.expectNone(isGet);
      });

    it('keeps the last successful data and warns when a later refresh fails', () => {
      const { fixture, http, element, component } = setup();
      component.refresh();
      http.expectOne(isGet).flush({}, { status: 500, statusText: 'Error' });
      fixture.detectChanges();

      expect(element.querySelector('.api-error')?.textContent).toContain('datos de la última consulta exitosa');
      expect(element.querySelector('.metrics')).not.toBeNull();
    });
  });

  describe('automatic refresh', () => {
    it('polls every 30 seconds', () => {
      const { http } = setup();

      vi.advanceTimersByTime(30_000);
      http.expectOne(isGet).flush(healthy());
      vi.advanceTimersByTime(30_000);
      http.expectOne(isGet).flush(healthy());
    });

    it('never overlaps requests: a tick is skipped while the previous one is in flight', () => {
      const { http } = setup();

      vi.advanceTimersByTime(30_000);
      const inFlight = http.expectOne(isGet);
      vi.advanceTimersByTime(60_000);
      http.expectNone(isGet);

      inFlight.flush(healthy());
      vi.advanceTimersByTime(30_000);
      http.expectOne(isGet).flush(healthy());
    });

    it('a manual refresh replaces the in-flight request and its stale response is discarded', () => {
      const { fixture, http, component } = setup();

      component.refresh();
      const stale = http.expectOne(isGet);
      component.refresh();
      const fresh = http.expectOne(isGet);

      expect(stale.cancelled).toBe(true);
      fresh.flush(healthy({ api: { status: 'Up', version: '9.9.9' } }));
      fixture.detectChanges();
      expect(component.health().api.version).toBe('9.9.9');
    });

    it('can be switched off and on', () => {
      const { http, component } = setup();

      component.toggleAutoRefresh(false);
      vi.advanceTimersByTime(90_000);
      http.expectNone(isGet);

      component.toggleAutoRefresh(true);
      vi.advanceTimersByTime(30_000);
      http.expectOne(isGet).flush(healthy());
    });

    it('stops polling and cancels the pending request when the screen is left', () => {
      const { fixture, http } = setup();
      vi.advanceTimersByTime(30_000);
      const pending = http.expectOne(isGet);

      fixture.destroy();

      expect(pending.cancelled).toBe(true);
      vi.advanceTimersByTime(120_000);
      http.expectNone(isGet);
    });

    it('stops polling and clears the data when the session is closed', () => {
      const { fixture, http, component } = setup();

      TestBed.inject(AuthService).logout();
      TestBed.tick();
      fixture.detectChanges();

      expect(component.isPolling).toBe(false);
      expect(component.health()).toBeNull();
      vi.advanceTimersByTime(120_000);
      http.expectNone(isGet);
    });
  });

  describe('permissions', () => {
    it('does not enable manual refresh for a non-Admin session and the API answer (403) is shown', () => {
      const { fixture, http, element, component } = setup('Seller', 'skip');
      http.expectOne(isGet).flush({}, { status: 403, statusText: 'Forbidden' });
      fixture.detectChanges();

      expect(component.isAdmin()).toBe(false);
      expect((Array.from(element.querySelectorAll('button')).find(b => b.textContent?.includes('Actualizar')) as HTMLButtonElement).disabled).toBe(true);
      expect(element.textContent).toContain('Solo los administradores');
    });
  });
});

describe('roleGuard for the system health route', () => {
  afterEach(() => sessionStorage.clear());

  function run(role: string) {
    sessionStorage.setItem('salesinventory.accessToken', tokenFor(role));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    return TestBed.runInInjectionContext(() => roleGuard({ data: { roles: ['Admin'] } } as never, {} as never));
  }

  it('lets Admin through', () => {
    expect(run('Admin')).toBe(true);
  });

  it('redirects Seller to the access denied page', () => {
    const result = run('Seller');
    expect(result).not.toBe(true);
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/access-denied');
  });
});

describe('system health helpers', () => {
  it('formats durations and keeps null as null', () => {
    expect(formatDuration(null)).toBeNull();
    expect(formatDuration(undefined)).toBeNull();
    expect(formatDuration(-1)).toBeNull();
    expect(formatDuration(0)).toBe('0 s');
    expect(formatDuration(45)).toBe('45 s');
    expect(formatDuration(603)).toBe('10 min 3 s');
    expect(formatDuration(3600)).toBe('1 h 0 min');
    expect(formatDuration(90000)).toBe('1 d 1 h');
  });

  it('scales the bars to the real maximum and stacks failures on top of processed', () => {
    const bars = buildBars([
      { bucketStartUtc: 'a', processed: 10, failed: 0 },
      { bucketStartUtc: 'b', processed: 20, failed: 20 }
    ], 100, 100);

    expect(bars[1].processedHeight).toBe(50);
    expect(bars[1].failedHeight).toBe(50);
    expect(bars[1].failedY).toBe(0);
    expect(bars[0].processedHeight).toBe(25);
    expect(buildBars([], 100, 100)).toEqual([]);
  });
});
