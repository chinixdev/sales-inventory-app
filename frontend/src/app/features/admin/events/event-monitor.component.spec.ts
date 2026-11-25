import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { roleGuard } from '../../../core/guards/auth.guard';
import { EventMonitorComponent } from './event-monitor.component';
import { fakeToken } from '../../../testing/fake-token';

const summary = { pending: 2, processing: 1, processed: 30, failed: 1, expiredLeases: 1, oldestPendingAtUtc: '2026-01-01T00:00:00Z' };
const FAILED_ID = '11111111-aaaa-bbbb-cccc-000000000001';
const PENDING_ID = '22222222-aaaa-bbbb-cccc-000000000002';
const item = (id: string, status: string, extra: Record<string, unknown> = {}) => ({
  id, eventType: 'SaleCompleted.v1', status, occurredAtUtc: '2026-01-02T10:00:00Z', processedAtUtc: null,
  retryCount: status === 'Failed' ? 5 : 0, nextAttemptAtUtc: '2026-01-02T10:05:00Z',
  lastError: status === 'Failed' ? 'InvalidOperationException: boom' : null, ...extra
});
const page = (items: unknown[], totalItems = items.length, totalPages = 1) => ({
  items, totalItems, pageNumber: 1, pageSize: 15, totalPages, hasPreviousPage: false, hasNextPage: false
});
const detail = {
  ...item(FAILED_ID, 'Failed'), lockedUntilUtc: null, leaseExpired: false, payloadBytes: 321, saleId: 42, itemCount: 2,
  consumers: [{ consumer: 'sales-inventory.stock-alerts', processedAtUtc: '2026-01-02T10:01:00Z' }]
};

describe('EventMonitorComponent', () => {
  const isSummary = (r: { url: string }) => r.url.endsWith('/outbox/summary');
  const isList = (r: { method: string; url: string }) => r.method === 'GET' && r.url.endsWith('/outbox/messages');

  function setup(role = 'Admin') {
    sessionStorage.setItem('salesinventory.accessToken', fakeToken(role));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(EventMonitorComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne(isSummary).flush(summary);
    http.expectOne(isList).flush(page([item(FAILED_ID, 'Failed'), item(PENDING_ID, 'Pending')]));
    fixture.detectChanges();
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => sessionStorage.clear());

  describe('indicators and table', () => {
    it('shows the four state indicators from the real summary endpoint', () => {
      const { element } = setup();
      const read = (selector: string) => Array.from(element.querySelectorAll(selector)).map(node => node.textContent?.trim());
      expect(read('.indicator .label')).toEqual(['Pending', 'Processing', 'Processed', 'Failed']);
      expect(read('.indicator .value')).toEqual(['2', '1', '30', '1']);
      expect(element.querySelector('.indicator.status-processing .hint')?.textContent).toContain('1 con lease vencido');
    });

    it('filters by status when an indicator is selected and sends all filters to the API', () => {
      const { http, component } = setup();

      component.filterByStatus('Failed');
      expect(http.expectOne(isList).request.params.get('status')).toBe('Failed');

      component.eventTypeFilter.set(' SaleCompleted.v1 ');
      component.fromDate.set('2026-01-01');
      component.toDate.set('2026-01-31');
      component.applyFilters();
      const params = http.expectOne(isList).request.params;
      expect(params.get('eventType')).toBe('SaleCompleted.v1');
      expect(params.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$/);
      expect(params.get('to')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$/);
      expect(params.get('pageNumber')).toBe('1');
    });

    it('shows the empty and error states of the list', () => {
      const { fixture, http, component, element } = setup();

      component.loadMessages();
      http.expectOne(isList).flush(page([]));
      fixture.detectChanges();
      expect(element.textContent).toContain('Aún no hay eventos registrados.');

      component.loadMessages();
      http.expectOne(isList).flush({}, { status: 500, statusText: 'Error' });
      fixture.detectChanges();
      expect(element.textContent).toContain('Reintentar');
    });
  });

  describe('detail', () => {
    it('shows safe technical information and never the payload', () => {
      const { fixture, http, component, element } = setup();

      component.showDetail(FAILED_ID);
      http.expectOne(r => r.url.endsWith(`/outbox/messages/${FAILED_ID}`)).flush(detail);
      fixture.detectChanges();

      const text = element.textContent ?? '';
      expect(text).toContain('321 bytes');
      expect(text).toContain('#42');
      expect(text).toContain('sales-inventory.stock-alerts');
      expect(text).toContain('Por seguridad no se muestra el contenido del evento.');
    });
  });

  describe('manual reprocessing', () => {
    it('offers the action only for Failed events', () => {
      const { element } = setup();
      const rows = Array.from(element.querySelectorAll('tbody tr'));
      expect(rows[0].textContent).toContain('Reprocesar');
      expect(rows[1].textContent).not.toContain('Reprocesar');
    });

    it('asks for confirmation, then POSTs to the real endpoint and refreshes the view', () => {
      const { fixture, http, component, element } = setup();

      component.requestRequeue({ id: FAILED_ID, eventType: 'SaleCompleted.v1', status: 'Failed' });
      fixture.detectChanges();
      expect(element.querySelector('[role="alertdialog"]')).not.toBeNull();
      http.expectNone(r => r.method === 'POST');

      component.confirmRequeue();
      http.expectOne(r => r.method === 'POST' && r.url.endsWith(`/outbox/messages/${FAILED_ID}/requeue`))
        .flush(null, { status: 204, statusText: 'No Content' });
      http.expectOne(isSummary).flush({ ...summary, pending: 3, failed: 0 });
      http.expectOne(isList).flush(page([item(FAILED_ID, 'Pending')]));
      fixture.detectChanges();

      expect(element.textContent).toContain('volvió a la cola (Pending)');
      expect(element.textContent).not.toContain('Reprocesar');
    });

    it('does nothing when the confirmation is cancelled', () => {
      const { fixture, http, component } = setup();

      component.requestRequeue({ id: FAILED_ID, eventType: 'SaleCompleted.v1', status: 'Failed' });
      component.pendingRequeue.set(null);
      component.confirmRequeue();
      fixture.detectChanges();

      http.expectNone(r => r.method === 'POST');
    });

    it('never requeues an event that is not Failed', () => {
      const { component } = setup();

      component.requestRequeue({ id: PENDING_ID, eventType: 'SaleCompleted.v1', status: 'Pending' });

      expect(component.pendingRequeue()).toBeNull();
    });

    it('shows the API conflict (409) and refreshes when another admin already requeued it', () => {
      const { fixture, http, component, element } = setup();
      component.requestRequeue({ id: FAILED_ID, eventType: 'SaleCompleted.v1', status: 'Failed' });

      component.confirmRequeue();
      http.expectOne(r => r.method === 'POST').flush(
        { detail: 'Solo se pueden reprocesar eventos en estado Failed.' },
        { status: 409, statusText: 'Conflict' }
      );
      http.expectOne(isSummary).flush(summary);
      http.expectOne(isList).flush(page([item(FAILED_ID, 'Pending')]));
      fixture.detectChanges();

      expect(element.textContent).toContain('Solo se pueden reprocesar eventos en estado Failed.');
    });

    it('does not expose the action to a non-Admin session', () => {
      const { element, component } = setup('Seller');

      expect(element.textContent).not.toContain('Reprocesar');
      component.requestRequeue({ id: FAILED_ID, eventType: 'SaleCompleted.v1', status: 'Failed' });
      expect(component.pendingRequeue()).toBeNull();
    });
  });
});

describe('roleGuard for the event monitor route', () => {
  afterEach(() => sessionStorage.clear());

  function run(role: string) {
    sessionStorage.setItem('salesinventory.accessToken', fakeToken(role));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    return TestBed.runInInjectionContext(() =>
      roleGuard({ data: { roles: ['Admin'] } } as never, {} as never)
    );
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
