import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StockMovementsComponent } from './stock-movements.component';
import { fakeToken } from '../../../../testing/fake-token';

const product = {
  id: 7, categoryId: 1, categoryName: 'Cat', sku: 'SKU-7', name: 'Producto 7', price: 10,
  stock: 4, minimumStock: 5, isActive: true, isBelowMinimumStock: true, createdAt: '2026-01-01T00:00:00Z'
};
const productPage = { items: [product], totalItems: 1, pageNumber: 1, pageSize: 100, totalPages: 1, hasPreviousPage: false, hasNextPage: false };
const emptyMovements = { items: [], totalItems: 0, pageNumber: 1, pageSize: 20, totalPages: 0, hasPreviousPage: false, hasNextPage: false };
const movement = {
  id: 1, productId: 7, productName: 'Producto 7', productSKU: 'SKU-7', userId: 1, userFullName: 'Admin',
  movementType: 'ADJUSTMENT', quantity: 6, previousStock: 4, newStock: 10, reason: 'Conteo físico', createdAt: '2026-01-01T00:00:00Z'
};

describe('StockMovementsComponent', () => {
  function render(role: string) {
    sessionStorage.setItem('salesinventory.accessToken', fakeToken(role));
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(StockMovementsComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('activeOnly') === 'true').flush(productPage);
    http.expectOne(r => r.url.endsWith('/inventory/movements')).flush(emptyMovements);
    fixture.detectChanges();
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => sessionStorage.clear());

  it('hides the registration form from Seller', () => {
    const { element } = render('Seller');
    expect(element.textContent).not.toContain('Registrar movimiento');
    expect(element.textContent).toContain('consultar los movimientos');
  });

  it('shows the form to Admin and loads the current stock of the selected product', () => {
    const { fixture, http, component, element } = render('Admin');
    expect(element.textContent).toContain('Registrar movimiento');

    component.onProductChange(7);
    http.expectOne(r => r.url.endsWith('/products/7')).flush(product);
    fixture.detectChanges();
    expect(element.querySelector('.stock-value')?.textContent).toContain('4');
  });

  it('asks for confirmation before sending an adjustment and then POSTs the real contract', () => {
    const { fixture, http, component, element } = render('Admin');
    component.onProductChange(7);
    http.expectOne(r => r.url.endsWith('/products/7')).flush(product);
    component.setOperation('adjustment');
    component.newStock.set(10);
    component.reason.set('Conteo físico');

    component.submit();
    fixture.detectChanges();
    http.expectNone(r => r.url.endsWith('/inventory/adjustments'));
    expect(element.textContent).toContain('stock actual 4 → nuevo stock 10 (+6)');

    component.confirmAdjustment();
    const request = http.expectOne(r => r.url.endsWith('/inventory/adjustments'));
    expect(request.request.body).toEqual({ productId: 7, newStock: 10, reason: 'Conteo físico' });
    request.flush(movement);
    http.expectOne(r => r.url.endsWith('/inventory/movements')).flush(emptyMovements);
    http.expectOne(r => r.url.endsWith('/products') && r.params.get('activeOnly') === 'true').flush(productPage);
    http.expectOne(r => r.url.endsWith('/products/7')).flush({ ...product, stock: 10 });
    fixture.detectChanges();
    expect(element.textContent).toContain('Stock: 4 → 10');
  });

  it('shows the backend conflict message on HTTP 409 and refreshes stock', () => {
    const { fixture, http, component, element } = render('Admin');
    component.onProductChange(7);
    http.expectOne(r => r.url.endsWith('/products/7')).flush(product);
    component.quantity.set(2);
    component.reason.set('Reposición');

    component.submit();
    http.expectOne(r => r.url.endsWith('/inventory/entries')).flush(
      { detail: 'El recurso fue modificado por otra transacción.' },
      { status: 409, statusText: 'Conflict' }
    );
    http.expectOne(r => r.url.endsWith('/products/7')).flush(product);
    http.expectOne(r => r.url.endsWith('/inventory/movements')).flush(emptyMovements);
    fixture.detectChanges();
    expect(element.textContent).toContain('El recurso fue modificado por otra transacción.');
  });
});
