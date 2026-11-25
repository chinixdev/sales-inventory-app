import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ProductListComponent } from './product-list.component';
import { fakeToken } from '../../../../testing/fake-token';

const page = {
  items: [{
    id: 1, categoryId: 1, categoryName: 'Cat', sku: 'SKU-1', name: 'Producto real', price: 10,
    stock: 1, minimumStock: 5, isActive: true, isBelowMinimumStock: true, createdAt: '2026-01-01T00:00:00Z'
  }],
  totalItems: 1, pageNumber: 1, pageSize: 10, totalPages: 1, hasPreviousPage: false, hasNextPage: false
};

describe('ProductListComponent', () => {
  function render(role: string) {
    sessionStorage.setItem('salesinventory.accessToken', fakeToken(role));
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ProductListComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.match(request => request.url.endsWith('/categories')).forEach(r => r.flush([]));
    http.expectOne(request => request.url.endsWith('/products')).flush(page);
    fixture.detectChanges();
    return { fixture, http, element: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => sessionStorage.clear());

  it('shows write actions to Admin', () => {
    const { element } = render('Admin');
    expect(element.textContent).toContain('Producto real');
    expect(element.textContent).toContain('Nuevo producto');
    expect(element.textContent).toContain('Eliminar');
  });

  it('hides write actions from Seller', () => {
    const { element } = render('Seller');
    expect(element.textContent).toContain('Producto real');
    expect(element.textContent).not.toContain('Nuevo producto');
    expect(element.textContent).not.toContain('Eliminar');
  });

  it('shows the API error state with retry', () => {
    sessionStorage.setItem('salesinventory.accessToken', fakeToken('Seller'));
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ProductListComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.match(request => request.url.endsWith('/categories')).forEach(r => r.flush([]));
    http.expectOne(request => request.url.endsWith('/products'))
      .flush({ title: 'x' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent;
    expect(text).toContain('x');
    expect(text).toContain('Reintentar');
  });
});
