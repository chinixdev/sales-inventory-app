import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { NotificationCenterComponent } from './notification-center.component';

const page = (items: unknown[], totalItems = items.length, totalPages = 1) => ({
  items, totalItems, pageNumber: 1, pageSize: 15, totalPages, hasPreviousPage: false, hasNextPage: false
});

const notification = (id: number, overrides: Record<string, unknown> = {}) => ({
  id, type: 'LowStock', productId: 7, title: 'Stock bajo', message: `Producto ${id} tiene 2 unidad(es)`,
  currentStock: 2, minimumStock: 5, createdAtUtc: '2026-01-02T10:00:00Z', isRead: false, readAtUtc: null, ...overrides
});

function tokenFor(role: string): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  return `${encode({ alg: 'HS256' })}.${encode({ role, nameid: '1', exp: Math.floor(Date.now() / 1000) + 600 })}.sig`;
}

describe('NotificationCenterComponent', () => {
  const isList = (r: { method: string; url: string }) => r.method === 'GET' && r.url.endsWith('/notifications');

  function setup() {
    sessionStorage.setItem('salesinventory.accessToken', tokenFor('Admin'));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(NotificationCenterComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => sessionStorage.clear());

  it('renders the real notifications returned by the API with the unread marker', () => {
    const { fixture, http, element } = setup();
    http.expectOne(isList).flush(page([notification(1), notification(2, { isRead: true, type: 'OutOfStock', title: 'Producto agotado' })]));
    fixture.detectChanges();

    const text = element.textContent ?? '';
    expect(text).toContain('Producto 1 tiene 2 unidad(es)');
    expect(text).toContain('Agotado');
    expect(element.querySelectorAll('li.notification').length).toBe(2);
    expect(element.querySelectorAll('li.notification.unread').length).toBe(1);
    expect(text).toContain('Marcar como leída');
  });

  it('maps the filters to the API query and returns to the first page', () => {
    const { fixture, http, component } = setup();
    http.expectOne(isList).flush(page([notification(1)], 40, 3));

    component.changePage(2);
    expect(http.expectOne(isList).request.params.get('pageNumber')).toBe('2');
    http.expectNone(isList);

    component.readFilter.set('unread');
    component.typeFilter.set('OutOfStock');
    component.applyFilters();
    const filtered = http.expectOne(isList).request.params;
    expect(filtered.get('unread')).toBe('true');
    expect(filtered.get('type')).toBe('OutOfStock');
    expect(filtered.get('pageNumber')).toBe('1');
    expect(filtered.get('pageSize')).toBe('15');
    fixture.detectChanges();
  });

  it('shows the empty state and the error state with retry', () => {
    const { fixture, http, element, component } = setup();
    http.expectOne(isList).flush(page([]));
    fixture.detectChanges();
    expect(element.textContent).toContain('No tienes notificaciones.');

    component.load();
    http.expectOne(isList).flush({}, { status: 500, statusText: 'Error' });
    fixture.detectChanges();
    expect(element.textContent).toContain('Reintentar');
  });

  it('marks a notification as read through the real endpoint and refreshes the counter', () => {
    const { fixture, http, component, element } = setup();
    http.expectOne(isList).flush(page([notification(5)]));
    fixture.detectChanges();

    component.markAsRead(component.items()[0]);
    const read = http.expectOne(r => r.method === 'POST' && r.url.endsWith('/notifications/5/read'));
    read.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(r => r.url.endsWith('/notifications/unread-count')).flush({ count: 0 });
    fixture.detectChanges();

    expect(element.querySelectorAll('li.notification.unread').length).toBe(0);
  });

  it('marks everything as read and reloads the list', () => {
    const { fixture, http, component, element } = setup();
    http.expectOne(isList).flush(page([notification(1), notification(2)]));

    component.markAllAsRead();
    http.expectOne(r => r.method === 'POST' && r.url.endsWith('/notifications/read-all')).flush({ updated: 2 });
    http.expectOne(isList).flush(page([notification(1, { isRead: true }), notification(2, { isRead: true })]));
    fixture.detectChanges();

    expect(element.textContent).toContain('2 notificación(es) marcada(s) como leída(s).');
    expect(element.querySelectorAll('li.notification.unread').length).toBe(0);
  });

  it('shows the API message when marking as read fails', () => {
    const { fixture, http, component, element } = setup();
    http.expectOne(isList).flush(page([notification(9)]));

    component.markAsRead(component.items()[0]);
    http.expectOne(r => r.url.endsWith('/notifications/9/read'))
      .flush({ detail: 'Notificación no encontrada.' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(element.textContent).toContain('Notificación no encontrada.');
  });
});
