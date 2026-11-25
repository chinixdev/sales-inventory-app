import { HttpHeaders, HttpParams } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { DEMO_USER } from './demo-data';
import { DemoBackend } from './demo-backend';

describe('DemoBackend', () => {
  let backend: DemoBackend;

  const call = (method: string, path: string, body: unknown = {}, query: Record<string, string> = {}, headers: Record<string, string> = {}) =>
    backend.handle(method, path, new HttpParams({ fromObject: query }), body, new HttpHeaders(headers));

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    backend = TestBed.inject(DemoBackend);
    backend.reset();
  });

  afterEach(() => localStorage.clear());

  /** El producto con más stock: así las ventas de prueba nunca dependen de los datos aleatorios del seed. */
  const firstProduct = () =>
    (call('GET', 'products', {}, { pageSize: '100' }).body as any).items.sort((a: any, b: any) => b.stock - a.stock)[0];

  describe('authentication', () => {
    it('accepts only the demo credentials and returns a decodable token', () => {
      const bad = call('POST', 'auth/login', { email: DEMO_USER.email, password: 'otra' });
      const good = call('POST', 'auth/login', { email: DEMO_USER.email, password: DEMO_USER.password });

      expect(bad.status).toBe(401);
      expect(good.status).toBe(200);
      const payload = JSON.parse(atob((good.body as any).accessToken.split('.')[1]));
      expect(payload.role).toBe('Admin');
    });
  });

  describe('catalog', () => {
    it('serves realistic seed data with the real paged contract', () => {
      const result = call('GET', 'products', {}, { pageSize: '5' }).body as any;

      expect(result.totalItems).toBeGreaterThan(10);
      expect(result.items).toHaveLength(5);
      expect(result).toEqual(expect.objectContaining({ pageNumber: 1, pageSize: 5, hasNextPage: true, hasPreviousPage: false }));
      expect(result.items[0]).toEqual(expect.objectContaining({ id: expect.any(Number), sku: expect.any(String), categoryName: expect.any(String) }));
    });

    it('filters products by search term and low stock', () => {
      const search = call('GET', 'products', {}, { searchTerm: 'teclado' }).body as any;
      const low = call('GET', 'products', {}, { lowStockOnly: 'true' }).body as any;

      expect(search.items.every((p: any) => /teclado/i.test(p.name))).toBe(true);
      expect(low.items.length).toBeGreaterThan(0);
      expect(low.items.every((p: any) => p.stock <= p.minimumStock)).toBe(true);
    });

    it('creates a product with an initial stock movement and rejects a duplicate SKU', () => {
      const category = (call('GET', 'categories').body as any[])[0];
      const body = { categoryId: category.id, sku: 'NEW-001', name: 'Producto nuevo', price: 10, initialStock: 7, minimumStock: 2 };

      const created = call('POST', 'products', body);
      const duplicate = call('POST', 'products', body);
      const movements = call('GET', 'inventory/movements', {}, { productId: String((created.body as any).id) }).body as any;

      expect(created.status).toBe(201);
      expect(duplicate.status).toBe(409);
      expect(movements.items).toHaveLength(1);
      expect(movements.items[0]).toEqual(expect.objectContaining({ movementType: 'IN', quantity: 7, newStock: 7 }));
    });

    it('does not delete a category that still has products', () => {
      const category = (call('GET', 'categories').body as any[])[0];

      expect(call('DELETE', `categories/${category.id}`).status).toBe(409);
    });
  });

  describe('inventory and sales', () => {
    it('registers a stock entry and a balanced movement', () => {
      const product = firstProduct();

      const reply = call('POST', 'inventory/entries', { productId: product.id, quantity: 5, reason: 'Reposición' });

      expect(reply.status).toBe(201);
      expect(reply.body).toEqual(expect.objectContaining({ previousStock: product.stock, newStock: product.stock + 5 }));
      expect((call('GET', `products/${product.id}`).body as any).stock).toBe(product.stock + 5);
    });

    it('creates a sale that deducts stock, records the movement and an outbox event', () => {
      const product = firstProduct();
      const outboxBefore = (call('GET', 'outbox/summary').body as any).processed;

      const sale = call('POST', 'sales', { items: [{ productId: product.id, quantity: 2 }] }, {}, { 'Idempotency-Key': 'key-1' });

      expect(sale.status).toBe(201);
      expect((sale.body as any).total).toBeCloseTo(product.price * 2);
      expect((call('GET', `products/${product.id}`).body as any).stock).toBe(product.stock - 2);
      const movement = (call('GET', 'inventory/movements', {}, { productId: String(product.id) }).body as any).items[0];
      expect(movement).toEqual(expect.objectContaining({ movementType: 'OUT', quantity: 2, saleId: (sale.body as any).id }));
      expect((call('GET', 'outbox/summary').body as any).processed).toBe(outboxBefore + 1);
    });

    it('rejects a sale without enough stock and leaves the stock untouched', () => {
      const product = firstProduct();

      const reply = call('POST', 'sales', { items: [{ productId: product.id, quantity: product.stock + 1 }] }, {}, { 'Idempotency-Key': 'key-2' });

      expect(reply.status).toBe(400);
      expect((reply.body as any).title).toBe('Stock insuficiente');
      expect((call('GET', `products/${product.id}`).body as any).stock).toBe(product.stock);
    });

    it('requires an Idempotency-Key and replays the original sale for the same key', () => {
      const product = firstProduct();
      const body = { items: [{ productId: product.id, quantity: 1 }] };

      expect(call('POST', 'sales', body).status).toBe(400);
      const first = call('POST', 'sales', body, {}, { 'Idempotency-Key': 'same' });
      const second = call('POST', 'sales', body, {}, { 'Idempotency-Key': 'same' });

      expect((second.body as any).id).toBe((first.body as any).id);
      expect((call('GET', `products/${product.id}`).body as any).stock).toBe(product.stock - 1);
    });

    it('raises one stock notification when a sale leaves a product at its minimum, and marks it as read', () => {
      const product = (call('GET', 'products', {}, { pageSize: '100' }).body as any).items
        .find((p: any) => p.stock > p.minimumStock);
      const unreadBefore = (call('GET', 'notifications/unread-count').body as any).count;

      call('POST', 'sales', { items: [{ productId: product.id, quantity: product.stock - product.minimumStock }] }, {}, { 'Idempotency-Key': 'low' });

      expect((call('GET', 'notifications/unread-count').body as any).count).toBe(unreadBefore + 1);
      expect((call('POST', 'notifications/read-all').body as any).updated).toBe(unreadBefore + 1);
      expect((call('GET', 'notifications/unread-count').body as any).count).toBe(0);
    });
  });

  describe('admin screens', () => {
    it('records audit entries for changes and exposes only safe fields', () => {
      const product = firstProduct();
      call('PUT', `products/${product.id}`, { ...product, price: 1 });

      const list = call('GET', 'audit-logs', {}, { entityName: 'Products', action: 'UPDATE' }).body as any;

      expect(list.items[0].changedFields).toEqual(['Price']);
      const detail = call('GET', `audit-logs/${list.items[0].id}`).body as any;
      expect(detail.newValues.Price).toBe(1);
    });

    it('lists outbox events and refuses to requeue one that is not Failed', () => {
      const events = call('GET', 'outbox/messages', {}, { pageSize: '1' }).body as any;

      expect(events.totalItems).toBeGreaterThan(0);
      expect(call('POST', `outbox/messages/${events.items[0].id}/requeue`).status).toBe(409);
    });

    it('returns a consistent, clearly demo, system health snapshot', () => {
      const health = call('GET', 'admin/system-health').body as any;

      expect(health.api.version).toBe('demo');
      expect(health.events.processed).toBeGreaterThan(0);
    });
  });

  describe('persistence and reset', () => {
    it('persists changes in localStorage and restores the seed on reset', () => {
      const product = firstProduct();
      call('POST', 'inventory/entries', { productId: product.id, quantity: 9, reason: 'Prueba' });

      const reloaded = new DemoBackend();
      const persisted = (reloaded.handle('GET', `products/${product.id}`, new HttpParams(), {}, new HttpHeaders()).body as any).stock;
      expect(persisted).toBe(product.stock + 9);

      reloaded.reset();
      expect((reloaded.handle('GET', `products/${product.id}`, new HttpParams(), {}, new HttpHeaders()).body as any).stock).toBe(product.stock);
    });

    it('starts from the seed when the stored data is corrupt', () => {
      localStorage.setItem('salesinventory.demo.state.v1', '{not json');

      const fresh = new DemoBackend();

      expect((fresh.handle('GET', 'products', new HttpParams(), {}, new HttpHeaders()).body as any).totalItems).toBeGreaterThan(10);
    });
  });
});
