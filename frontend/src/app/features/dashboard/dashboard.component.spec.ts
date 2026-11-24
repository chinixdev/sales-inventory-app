import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DashboardComponent } from './dashboard.component';

const page = <T>(items: T[], totalItems = items.length, totalPages = 1, pageSize = 100) =>
  ({ items, totalItems, pageNumber: 1, pageSize, totalPages, hasPreviousPage: false, hasNextPage: false });

const sale = (id: number, total: number) => ({
  id, userId: 1, userFullName: 'Vendedor', saleDate: new Date().toISOString(), total,
  details: [{ id: id, productId: 1, productName: 'P', productSKU: 'S', quantity: 1, unitPrice: total, subtotal: total }]
});
const lowProduct = (id: number, stock: number) => ({
  id, categoryId: 1, sku: `S${id}`, name: `Prod ${id}`, price: 1, stock, minimumStock: 5,
  isActive: true, isBelowMinimumStock: true, createdAt: '2026-01-01T00:00:00Z'
});

describe('DashboardComponent', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(DashboardComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return { fixture, http, element: fixture.nativeElement as HTMLElement };
  }

  it('computes real indicators in PEN from all pages of the week', () => {
    const { fixture, http, element } = setup();

    const weekPages = http.match(r => r.url.endsWith('/sales') && r.params.has('from'));
    expect(weekPages.length).toBe(1);
    weekPages[0].flush(page([sale(1, 100), sale(2, 50.5)], 2));
    http.expectOne(r => r.url.endsWith('/sales') && !r.params.has('from')).flush(page([sale(2, 50.5), sale(1, 100)], 2, 1, 5));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('pageSize') === '1').flush(page([], 12, 12, 1));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('lowStockOnly') === 'true')
      .flush(page([lowProduct(1, 0), lowProduct(2, 3)], 2));
    http.expectOne(r => r.url.endsWith('/health')).flush({ status: 'Healthy', service: 'SalesInventory.Api' });
    fixture.detectChanges();

    const text = element.textContent ?? '';
    expect(text).toContain('150.50');
    expect(text).toContain('S/');
    expect(text).toContain('12');
    expect(text).toContain('1 agotado');
    expect(text).toContain('Agotado');
    expect(element.querySelector('[role="img"]')).not.toBeNull();
  });

  it('does not present partial sums when there are too many pages', () => {
    const { fixture, http, element } = setup();

    http.match(r => r.url.endsWith('/sales') && r.params.has('from'))[0].flush(page([sale(1, 100)], 5000, 50));
    http.expectOne(r => r.url.endsWith('/sales') && !r.params.has('from')).flush(page([], 0, 0, 5));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('pageSize') === '1').flush(page([], 1, 1, 1));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('lowStockOnly') === 'true').flush(page([]));
    http.expectOne(r => r.url.endsWith('/health')).flush({ status: 'Healthy', service: 'x' });
    fixture.detectChanges();

    expect(element.textContent).toContain('No verificable');
    expect(element.textContent).toContain('Gráfico no disponible');
    expect(element.querySelector('[role="img"]')).toBeNull();
  });

  it('shows per-section errors without breaking the page', () => {
    const { fixture, http, element } = setup();

    http.match(r => r.url.endsWith('/sales') && r.params.has('from'))[0].flush({}, { status: 500, statusText: 'Err' });
    http.expectOne(r => r.url.endsWith('/sales') && !r.params.has('from')).flush(page([]));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('pageSize') === '1').flush(page([], 3, 3, 1));
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('lowStockOnly') === 'true').flush(page([]));
    http.expectOne(r => r.url.endsWith('/health')).flush({}, { status: 500, statusText: 'Err' });
    fixture.detectChanges();

    expect(element.textContent).toContain('No se pudieron cargar las ventas de la semana');
    expect(element.textContent).toContain('Aún no hay ventas registradas.');
    expect(element.textContent).toContain('API no disponible');
  });
});
