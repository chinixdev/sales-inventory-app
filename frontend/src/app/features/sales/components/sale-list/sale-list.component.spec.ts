import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SaleListComponent } from './sale-list.component';

const product = {
  id: 3, categoryId: 1, categoryName: 'Cat', sku: 'SKU-3', name: 'Producto 3', price: 12.5,
  stock: 4, minimumStock: 1, isActive: true, isBelowMinimumStock: false, createdAt: '2026-01-01T00:00:00Z'
};
const page = <T>(items: T[]) => ({ items, totalItems: items.length, pageNumber: 1, pageSize: 20, totalPages: 1, hasPreviousPage: false, hasNextPage: false });
const sale = {
  id: 55, userId: 1, userFullName: 'Vendedor', saleDate: '2026-01-01T00:00:00Z', total: 25,
  details: [{ id: 1, productId: 3, productName: 'Producto 3', productSKU: 'SKU-3', quantity: 2, unitPrice: 12.5, subtotal: 25 }]
};

describe('SaleListComponent', () => {
  function render() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(SaleListComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne(r => r.url.endsWith('/products')).flush(page([product]));
    http.expectOne(r => r.url.endsWith('/sales')).flush(page([]));
    fixture.detectChanges();
    return { fixture, http, component: fixture.componentInstance as any, element: fixture.nativeElement as HTMLElement };
  }

  it('does not duplicate products: adding twice increments the same line', () => {
    const { component } = render();
    component.addToCart(product);
    component.addToCart(product);
    expect(component.cart().length).toBe(1);
    expect(component.cart()[0].quantity).toBe(2);
    expect(component.estimatedTotal()).toBe(25);
  });

  it('blocks invalid quantities and quantities above the available stock', () => {
    const { component } = render();
    component.addToCart(product);
    component.setQuantity(3, 0);
    expect(component.canSubmit()).toBe(false);
    component.setQuantity(3, 5);
    expect(component.canSubmit()).toBe(false);
    component.setQuantity(3, 4);
    expect(component.canSubmit()).toBe(true);
  });

  it('confirms first, then POSTs only items (no UserId) and shows the API summary', () => {
    const { fixture, http, component, element } = render();
    component.addToCart(product);
    component.setQuantity(3, 2);

    component.requestSale();
    http.expectNone(r => r.method === 'POST');
    component.confirmSale();
    const request = http.expectOne(r => r.method === 'POST' && r.url.endsWith('/sales'));
    expect(request.request.body).toEqual({ items: [{ productId: 3, quantity: 2 }] });
    request.flush(sale);
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/sales')).flush(page([sale]));
    http.expectOne(r => r.url.endsWith('/products')).flush(page([product]));
    fixture.detectChanges();

    expect(element.textContent).toContain('Venta #55 registrada');
    expect(component.cart().length).toBe(0);
  });

  it('shows the error and refreshes stock on HTTP 409', () => {
    const { fixture, http, component, element } = render();
    component.addToCart(product);
    component.confirmSale();
    http.expectOne(r => r.method === 'POST').flush(
      { detail: 'Conflicto de concurrencia.' },
      { status: 409, statusText: 'Conflict' }
    );
    http.expectOne(r => r.url.endsWith('/products/3')).flush({ ...product, stock: 0 });
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/products')).flush(page([]));
    fixture.detectChanges();

    expect(element.textContent).toContain('Conflicto de concurrencia.');
    expect(component.cart()[0].stock).toBe(0);
    expect(component.canSubmit()).toBe(false);
  });

  describe('Idempotency-Key', () => {
    const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
    const isSalePost = (r: { method: string; url: string }) => r.method === 'POST' && r.url.endsWith('/sales');

    function prepare(quantity = 2) {
      const ctx = render();
      ctx.component.addToCart(product);
      ctx.component.setQuantity(3, quantity);
      return ctx;
    }

    function finishSuccess(http: any) {
      http.expectOne((r: any) => r.method === 'GET' && r.url.endsWith('/sales')).flush(page([sale]));
      http.expectOne((r: any) => r.url.endsWith('/products')).flush(page([product]));
    }

    it('sends a UUID in the Idempotency-Key header', () => {
      const { http, component } = prepare();
      component.requestSale();
      component.confirmSale();

      const request = http.expectOne(isSalePost);
      expect(request.request.headers.get('Idempotency-Key')).toMatch(uuid);
      request.flush(sale);
      finishSuccess(http);
    });

    it('sends only one request when the confirmation is triggered twice (double click)', () => {
      const { http, component } = prepare();
      component.requestSale();
      component.confirmSale();
      component.confirmSale();
      component.requestSale();

      const requests = http.match(isSalePost);
      expect(requests.length).toBe(1);
      requests[0].flush(sale);
      finishSuccess(http);
    });

    it('reuses the same key when retrying the identical sale after a network failure', () => {
      const { http, component } = prepare();
      component.requestSale();
      component.confirmSale();
      const first = http.expectOne(isSalePost);
      const key = first.request.headers.get('Idempotency-Key');
      first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      expect(component.canSubmit()).toBe(true);
      component.requestSale();
      component.confirmSale();
      const retry = http.expectOne(isSalePost);
      expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
      retry.flush(sale);
      finishSuccess(http);
    });

    it('generates a new key when the cart content changes', () => {
      const { http, component } = prepare(2);
      component.requestSale();
      component.confirmSale();
      const first = http.expectOne(isSalePost);
      const firstKey = first.request.headers.get('Idempotency-Key');
      first.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

      component.setQuantity(3, 3);
      component.requestSale();
      component.confirmSale();
      const second = http.expectOne(isSalePost);
      expect(second.request.headers.get('Idempotency-Key')).not.toBe(firstKey);
      second.flush(sale);
      finishSuccess(http);
    });

    it('clears the key after a confirmed sale so the next sale uses a new one', () => {
      const { http, component } = prepare(2);
      component.requestSale();
      component.confirmSale();
      const first = http.expectOne(isSalePost);
      const firstKey = first.request.headers.get('Idempotency-Key');
      first.flush(sale);
      finishSuccess(http);
      expect(component.cart().length).toBe(0);

      component.addToCart(product);
      component.setQuantity(3, 2);
      component.requestSale();
      component.confirmSale();
      const second = http.expectOne(isSalePost);
      expect(second.request.headers.get('Idempotency-Key')).not.toBe(firstKey);
      second.flush(sale);
      finishSuccess(http);
    });

    it('does not persist the key in web storage', () => {
      localStorage.clear();
      sessionStorage.clear();
      const { http, component } = prepare();
      component.requestSale();
      component.confirmSale();
      const request = http.expectOne(isSalePost);
      const key = request.request.headers.get('Idempotency-Key')!;

      expect(JSON.stringify({ ...localStorage })).not.toContain(key);
      expect(JSON.stringify({ ...sessionStorage })).not.toContain(key);
      request.flush(sale);
      finishSuccess(http);
    });
  });
});
