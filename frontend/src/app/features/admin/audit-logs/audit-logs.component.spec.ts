import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { roleGuard } from '../../../core/guards/auth.guard';
import { AuditLogsComponent } from './audit-logs.component';
import { buildValueRows, formatAuditValue } from './audit-values';

function tokenFor(role: string): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  return `${encode({ alg: 'HS256' })}.${encode({ role, nameid: '1', exp: Math.floor(Date.now() / 1000) + 600 })}.sig`;
}

const item = (id: number, extra: Record<string, unknown> = {}) => ({
  id, entityName: 'Products', entityId: '10', action: 'UPDATE', userId: 3, actorType: 'User', source: 'API',
  changedFields: ['Price'], occurredAtUtc: '2026-01-02T10:00:00Z', correlationId: `corr-${id}`, ...extra
});
const page = (items: unknown[], totalItems = items.length, totalPages = 1, pageNumber = 1) => ({
  items, totalItems, pageNumber, pageSize: 20, totalPages, hasPreviousPage: pageNumber > 1, hasNextPage: pageNumber < totalPages
});
const updateDetail = {
  ...item(1), oldValues: { Price: 10, Name: 'Teclado' }, newValues: { Price: 12, Name: 'Teclado' }
};

describe('AuditLogsComponent', () => {
  const isList = (r: { method: string; url: string }) => r.method === 'GET' && r.url.endsWith('/audit-logs');
  const isDetail = (id: number) => (r: { url: string }) => r.url.endsWith(`/audit-logs/${id}`);

  function setup(role = 'Admin', first: object = page([item(1), item(2, { action: 'INSERT', actorType: 'System', userId: null, source: 'Worker' })])) {
    sessionStorage.setItem('salesinventory.accessToken', tokenFor(role));
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(AuditLogsComponent);
    document.body.appendChild(fixture.nativeElement);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne(isList).flush(first);
    fixture.detectChanges();
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => {
    document.body.replaceChildren();
    sessionStorage.clear();
  });

  describe('table', () => {
    it('shows entity, identifier, action, actor, source and date', () => {
      const { element } = setup();
      const rows = Array.from(element.querySelectorAll('tbody tr'));
      expect(rows).toHaveLength(2);
      expect(rows[0].textContent).toContain('Products');
      expect(rows[0].textContent).toContain('10');
      expect(rows[0].textContent).toContain('UPDATE');
      expect(rows[0].textContent).toContain('Usuario #3');
      expect(rows[0].textContent).toContain('API');
      expect(rows[1].textContent).toContain('Sistema');
      expect(rows[1].textContent).toContain('Worker');
    });

    it('is read-only: only the detail action exists', () => {
      const { element } = setup();
      const labels = Array.from(element.querySelectorAll('tbody button')).map(b => b.textContent?.trim());
      expect(new Set(labels)).toEqual(new Set(['Detalle']));
      expect(element.textContent).not.toMatch(/Editar|Eliminar|Borrar/);
    });

    it('requests the first page with the page size of the contract', () => {
      const { http, component } = setup();
      component.loadLogs();
      const params = http.expectOne(isList).request.params;
      expect(params.get('pageNumber')).toBe('1');
      expect(params.get('pageSize')).toBe('20');
    });
  });

  describe('states', () => {
    it('shows the empty state, with and without filters', () => {
      const { fixture, http, component, element } = setup('Admin', page([]));
      expect(element.textContent).toContain('Aún no hay registros de auditoría.');

      component.entityFilter.set('Users');
      component.applyFilters();
      http.expectOne(isList).flush(page([]));
      fixture.detectChanges();
      expect(element.textContent).toContain('Ningún registro coincide con los filtros.');
    });

    it('shows loading while the request is pending', () => {
      const { fixture, http, component, element } = setup();
      component.loadLogs();
      fixture.detectChanges();
      expect(element.textContent).toContain('Cargando auditoría…');
      http.expectOne(isList).flush(page([]));
    });

    it('shows a generic error with retry', () => {
      const { fixture, http, component, element } = setup();
      component.loadLogs();
      http.expectOne(isList).flush({}, { status: 500, statusText: 'Error' });
      fixture.detectChanges();
      expect(element.textContent).toContain('Reintentar');
      expect(component.logs()).toEqual([]);
    });

    it('shows the 401 and 403 messages without offering retry', () => {
      const { fixture, http, component, element } = setup();

      component.loadLogs();
      http.expectOne(isList).flush({}, { status: 401, statusText: 'Unauthorized' });
      fixture.detectChanges();
      expect(element.textContent).toContain('Tu sesión no es válida o expiró');
      expect(element.textContent).not.toContain('Reintentar');

      component.loadLogs();
      http.expectOne(isList).flush({}, { status: 403, statusText: 'Forbidden' });
      fixture.detectChanges();
      expect(element.textContent).toContain('Solo los administradores');
      expect(element.textContent).not.toContain('Reintentar');
    });

    it('shows the 404 of a record that no longer exists inside the detail panel', () => {
      const { fixture, http, component, element } = setup();
      component.openDetail(99);
      http.expectOne(isDetail(99)).flush({}, { status: 404, statusText: 'Not Found' });
      fixture.detectChanges();
      expect(element.querySelector('[role="dialog"]')?.textContent).toContain('no existe');
    });
  });

  describe('filters and pagination', () => {
    it('sends every real backend filter and resets to the first page', () => {
      const { http, component } = setup();
      component.pageNumber.set(3);
      component.entityFilter.set('Sales');
      component.actionFilter.set('DELETE');
      component.userFilter.set(' 7 ');
      component.fromDate.set('2026-01-01');
      component.toDate.set('2026-01-31');
      component.correlationFilter.set(' abc-123 ');

      component.applyFilters();

      const params = http.expectOne(isList).request.params;
      expect(params.get('pageNumber')).toBe('1');
      expect(params.get('entityName')).toBe('Sales');
      expect(params.get('action')).toBe('DELETE');
      expect(params.get('userId')).toBe('7');
      expect(params.get('correlationId')).toBe('abc-123');
      expect(params.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$/);
      expect(params.get('to')).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}$/);
    });

    it('does not send empty filters', () => {
      const { http, component } = setup();
      component.applyFilters();
      const params = http.expectOne(isList).request.params;
      expect(params.keys().sort()).toEqual(['pageNumber', 'pageSize']);
    });

    it('rejects a non numeric user and an inverted date range without calling the API', () => {
      const { fixture, http, component, element } = setup();

      component.userFilter.set('abc');
      component.applyFilters();
      fixture.detectChanges();
      expect(element.textContent).toContain('identificador numérico positivo');

      component.userFilter.set('');
      component.fromDate.set('2026-02-01');
      component.toDate.set('2026-01-01');
      component.applyFilters();
      fixture.detectChanges();
      expect(element.textContent).toContain('no puede ser posterior');
      http.expectNone(isList);
    });

    it('clears the filters and reloads', () => {
      const { http, component } = setup();
      component.entityFilter.set('Users');
      component.clearFilters();
      const params = http.expectOne(isList).request.params;
      expect(params.has('entityName')).toBe(false);
      expect(component.hasFilters()).toBe(false);
    });

    it('navigates between pages and never outside the real range', () => {
      const { fixture, http, component, element } = setup('Admin', page([item(1)], 45, 3));
      expect(element.textContent).toContain('Página 1 de 3 (45 registros)');

      component.changePage(0);
      component.changePage(4);
      http.expectNone(isList);

      component.changePage(2);
      const request = http.expectOne(isList);
      expect(request.request.params.get('pageNumber')).toBe('2');
      request.flush(page([item(21)], 45, 3, 2));
      fixture.detectChanges();
      expect(element.textContent).toContain('Página 2 de 3');
    });

    it('ignores the stale response of a superseded query', () => {
      const { http, component } = setup();
      component.loadLogs();
      const first = http.expectOne(isList);
      component.entityFilter.set('Users');
      component.applyFilters();
      const second = http.expectOne(isList);

      second.flush(page([item(50)]));
      expect(first.cancelled).toBe(true);
      expect(component.logs().map((l: { id: number }) => l.id)).toEqual([50]);
    });

    it('shows no global metrics, only the real total of the contract', () => {
      const { element } = setup('Admin', page([item(1)], 1));
      expect(element.querySelector('.indicators, .indicator')).toBeNull();
      expect(element.textContent).toContain('1 registro(s)');
    });
  });

  describe('detail', () => {
    it('opens an accessible dialog, moves focus to it and highlights only modified fields in an UPDATE', async () => {
      const { fixture, http, component, element } = setup();
      const opener = element.querySelector('tbody button') as HTMLButtonElement;
      opener.focus();

      opener.click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();
      await new Promise(resolve => setTimeout(resolve));

      const dialog = element.querySelector('[role="dialog"]') as HTMLElement;
      expect(dialog.getAttribute('aria-modal')).toBe('true');
      expect(dialog.getAttribute('aria-labelledby')).toBe('audit-detail-title');
      expect(document.activeElement).toBe(dialog);

      const rows = Array.from(dialog.querySelectorAll('tbody tr'));
      expect(rows).toHaveLength(2);
      const price = rows.find(r => r.textContent?.includes('Price')) as HTMLElement;
      const name = rows.find(r => r.textContent?.includes('Name')) as HTMLElement;
      expect(price.classList.contains('changed')).toBe(true);
      expect(price.textContent).toContain('modificado');
      expect(price.querySelector('.old')?.textContent?.trim()).toBe('10');
      expect(price.querySelector('.new')?.textContent?.trim()).toBe('12');
      expect(name.classList.contains('changed')).toBe(false);
      expect(component.valueRows().length).toBe(2);
    });

    it('closes with Escape and returns the focus to the row button', async () => {
      const { fixture, http, element } = setup();
      const opener = element.querySelector('tbody button') as HTMLButtonElement;
      opener.focus();
      opener.click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      await new Promise(resolve => setTimeout(resolve));

      expect(element.querySelector('[role="dialog"]')).toBeNull();
      expect(document.activeElement).toBe(opener);
    });

    it('keeps the focus inside the dialog with Tab and Shift+Tab', async () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();
      const dialog = element.querySelector('[role="dialog"]') as HTMLElement;
      const buttons = Array.from(dialog.querySelectorAll('button'));
      const last = buttons[buttons.length - 1];

      last.focus();
      dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
      expect(document.activeElement).toBe(buttons[0]);

      dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
      expect(document.activeElement).toBe(last);
    });

    it('shows an INSERT with only the new values', () => {
      const { fixture, http, element } = setup();
      (element.querySelectorAll('tbody button')[1] as HTMLButtonElement).click();
      http.expectOne(isDetail(2)).flush({
        ...item(2, { action: 'INSERT', changedFields: ['Name', 'Stock'] }), oldValues: null, newValues: { Name: 'Mouse', Stock: 4 }
      });
      fixture.detectChanges();

      const dialog = element.querySelector('[role="dialog"]') as HTMLElement;
      expect(Array.from(dialog.querySelectorAll('thead th')).map(h => h.textContent?.trim())).toEqual(['Campo', 'Nuevo']);
      expect(dialog.textContent).toContain('Valores creados');
      expect(dialog.textContent).toContain('Mouse');
      expect(dialog.querySelector('tr.changed')).toBeNull();
    });

    it('shows a DELETE with only the old values', () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush({
        ...item(1, { action: 'DELETE', changedFields: ['Name'] }), oldValues: { Name: 'Mouse' }, newValues: null
      });
      fixture.detectChanges();

      const dialog = element.querySelector('[role="dialog"]') as HTMLElement;
      expect(Array.from(dialog.querySelectorAll('thead th')).map(h => h.textContent?.trim())).toEqual(['Campo', 'Anterior']);
      expect(dialog.textContent).toContain('Valores eliminados');
      expect(dialog.textContent).toContain('Mouse');
    });

    it('renders null values and unknown properties without errors', () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush({
        ...item(1, { changedFields: ['Description'] }),
        oldValues: { Description: null, Mystery: { a: 1 } },
        newValues: { Description: 'Nuevo', Mystery: { a: 1 }, Extra: [1, 2] }
      });
      fixture.detectChanges();

      const text = element.querySelector('[role="dialog"]')?.textContent ?? '';
      expect(text).toContain('sin valor');
      expect(text).toContain('Mystery');
      expect(text).toContain('{"a":1}');
      expect(text).toContain('[1,2]');
    });

    it('tolerates a record without values and without changedFields', () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush({ ...item(1), changedFields: undefined, oldValues: undefined, newValues: undefined });
      fixture.detectChanges();
      expect(element.querySelector('[role="dialog"]')?.textContent).toContain('no contiene valores');
    });

    it('shows the CorrelationId and copies it', async () => {
      const { fixture, http, element } = setup();
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();

      expect(element.querySelector('[role="dialog"] .correlation code')?.textContent).toBe('corr-1');
      const copy = Array.from(element.querySelectorAll('[role="dialog"] button')).find(b => b.textContent?.trim() === 'Copiar') as HTMLButtonElement;
      copy.click();
      await Promise.resolve();
      await Promise.resolve();
      fixture.detectChanges();

      expect(writeText).toHaveBeenCalledWith('corr-1');
      expect(element.querySelector('[role="status"].copy-notice')?.textContent).toContain('copiado');
    });

    it('tells the user when copying is not possible instead of failing', async () => {
      const { fixture, http, element } = setup();
      Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) }, configurable: true });
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();

      const copy = Array.from(element.querySelectorAll('[role="dialog"] button')).find(b => b.textContent?.trim() === 'Copiar') as HTMLButtonElement;
      copy.click();
      await new Promise(resolve => setTimeout(resolve));
      fixture.detectChanges();

      expect(element.querySelector('.copy-notice')?.textContent).toContain('No se pudo copiar');
    });

    it('filters the list by the CorrelationId of the opened record', () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();

      const view = Array.from(element.querySelectorAll('[role="dialog"] button')).find(b => b.textContent?.trim() === 'Ver operación') as HTMLButtonElement;
      view.click();

      expect(http.expectOne(isList).request.params.get('correlationId')).toBe('corr-1');
      fixture.detectChanges();
      expect(element.querySelector('[role="dialog"]')).toBeNull();
    });

    it('never renders write actions in the dialog', () => {
      const { fixture, http, element } = setup();
      (element.querySelector('tbody button') as HTMLButtonElement).click();
      http.expectOne(isDetail(1)).flush(updateDetail);
      fixture.detectChanges();

      const labels = Array.from(element.querySelectorAll('[role="dialog"] button')).map(b => b.textContent?.trim());
      expect(labels).toEqual(['Cerrar', 'Copiar', 'Ver operación']);
    });
  });

  describe('permissions', () => {
    it('does not issue any write request', () => {
      const { http } = setup();
      http.expectNone(r => r.method !== 'GET');
    });
  });
});

describe('value comparison helpers', () => {
  it('formats null, scalars and objects safely', () => {
    expect(formatAuditValue(null)).toBeNull();
    expect(formatAuditValue(undefined)).toBeNull();
    expect(formatAuditValue(false)).toBe('false');
    expect(formatAuditValue(0)).toBe('0');
    expect(formatAuditValue({ a: 1 })).toBe('{"a":1}');
    const circular: Record<string, unknown> = {};
    circular['self'] = circular;
    expect(formatAuditValue(circular)).toBe('[valor no representable]');
  });

  it('ignores non-object value payloads', () => {
    expect(buildValueRows('texto', [1, 2], [], 'UPDATE')).toEqual([]);
  });

  it('marks as changed a field listed by the backend or whose values differ, in UPDATE only', () => {
    const rows = buildValueRows({ A: 1, B: 2, C: 3 }, { A: 1, B: 9, C: 3 }, ['C'], 'UPDATE');
    expect(rows.map(r => [r.field, r.changed])).toEqual([['A', false], ['B', true], ['C', true]]);
    expect(buildValueRows(null, { A: 1 }, ['A'], 'INSERT')[0].changed).toBe(false);
  });
});

describe('roleGuard for the audit route', () => {
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
