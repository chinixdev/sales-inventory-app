import { HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { AuditLogDetail } from '../models/audit-log.model';
import { PagedResult } from '../models/paged-result.model';
import { SystemHealth } from '../models/system-health.model';
import { Product } from '../../features/products/models/product.model';
import { generateUuid } from '../utils/uuid';
import { createSeedState, DEMO_USER, DemoState, eventIdFor, stockNotification } from './demo-data';

export interface DemoReply {
  status: number;
  body: unknown;
}

const STORAGE_KEY = 'salesinventory.demo.state.v1';
const ok = (body: unknown, status = 200): DemoReply => ({ status, body });
const problem = (status: number, title: string, detail: string): DemoReply => ({ status, body: { status, title, detail } });
const notFound = (what: string) => problem(404, 'Recurso no encontrado', `${what} no existe.`);
const nowIso = () => new Date().toISOString();
const round = (value: number) => Math.round(value * 100) / 100;

/**
 * API simulada del modo demo. Reproduce los contratos de la API real con datos ficticios que viven solo en el navegador
 * (localStorage). No hay red ni base de datos: cada operación modifica el estado y lo persiste. Es una herramienta de
 * demostración, no una réplica de las reglas completas del backend.
 */
@Injectable({ providedIn: 'root' })
export class DemoBackend {
  private state: DemoState = this.load();

  /** Vuelve a los datos de ejemplo originales. */
  reset(): void {
    this.state = createSeedState();
    this.persist();
  }

  handle(method: string, path: string, params: HttpParams, body: any, headers: HttpHeaders): DemoReply {
    const [resource, id, action] = path.split('?')[0].split('/');
    const reply = this.route(method, resource, id, action, params, body ?? {}, headers);
    if (method !== 'GET') {
      this.persist();
    }
    return reply;
  }

  // ─── Enrutado ───

  private route(method: string, resource: string, id: string | undefined, action: string | undefined,
                params: HttpParams, body: any, headers: HttpHeaders): DemoReply {
    switch (resource) {
      case 'auth': return this.login(body);
      case 'categories': return this.categories(method, id, params, body);
      case 'products': return this.products(method, id, params, body);
      case 'inventory': return this.inventory(method, id, params, body);
      case 'sales': return this.sales(method, id, params, body, headers);
      case 'notifications': return this.notifications(method, id, action, params);
      case 'outbox': return this.outbox(method, id, action, params);
      case 'audit-logs': return this.auditLogs(id, params);
      case 'admin': return ok(this.systemHealth());
      case 'health': return ok({ status: 'Healthy' });
      default: return notFound('El endpoint');
    }
  }

  // ─── Autenticación ───

  private login(body: { email?: string; password?: string }): DemoReply {
    if (body.email?.toLowerCase() !== DEMO_USER.email || body.password !== DEMO_USER.password) {
      return problem(401, 'No autorizado', 'Credenciales inválidas.');
    }

    // JWT sin firma: la aplicación solo lee los claims; nada en el modo demo se valida contra un servidor.
    const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
    const expires = Date.now() + 8 * 3_600_000;
    const claims = { role: 'Admin', nameid: String(DEMO_USER.id), email: DEMO_USER.email, exp: Math.floor(expires / 1000) };
    return ok({
      accessToken: `${encode({ alg: 'none' })}.${encode(claims)}.demo`,
      tokenType: 'Bearer',
      expiresAt: new Date(expires).toISOString()
    });
  }

  // ─── Categorías ───

  private categories(method: string, id: string | undefined, params: HttpParams, body: any): DemoReply {
    const s = this.state;
    const view = (c: DemoState['categories'][number]) => ({ ...c, productCount: s.products.filter(p => p.categoryId === c.id).length });

    if (method === 'GET' && !id) {
      const activeOnly = params.get('activeOnly') === 'true';
      return ok(s.categories.filter(c => !activeOnly || c.isActive).map(view));
    }

    const category = s.categories.find(c => c.id === Number(id));
    if (method === 'GET') return category ? ok(view(category)) : notFound('La categoría');

    if (method === 'POST') {
      const name = String(body.name ?? '').trim();
      if (name.length < 2) return problem(400, 'Error de validación', 'El nombre debe tener entre 2 y 100 caracteres.');
      if (s.categories.some(c => c.name.toLowerCase() === name.toLowerCase())) return problem(409, 'Conflicto de negocio', 'Ya existe una categoría con ese nombre.');
      const created = { id: s.nextId.category++, name, isActive: true };
      s.categories.push(created);
      this.audit('Categories', created.id, 'INSERT', null, { Name: name });
      return ok(view(created), 201);
    }

    if (!category) return notFound('La categoría');
    if (method === 'PUT') {
      const before = { Name: category.name, IsActive: category.isActive };
      category.name = String(body.name ?? category.name).trim();
      category.isActive = body.isActive ?? category.isActive;
      this.audit('Categories', category.id, 'UPDATE', before, { Name: category.name, IsActive: category.isActive });
      return ok(view(category));
    }

    if (s.products.some(p => p.categoryId === category.id)) return problem(409, 'Conflicto de negocio', 'No se puede eliminar una categoría con productos.');
    s.categories = s.categories.filter(c => c !== category);
    this.audit('Categories', category.id, 'DELETE', { Name: category.name }, null);
    return ok(null, 204);
  }

  // ─── Productos ───

  private productView(p: Product): Product {
    const category = this.state.categories.find(c => c.id === p.categoryId);
    return { ...p, categoryName: category?.name ?? p.categoryName, isBelowMinimumStock: p.stock <= p.minimumStock };
  }

  private products(method: string, id: string | undefined, params: HttpParams, body: any): DemoReply {
    const s = this.state;

    if (method === 'GET' && !id) {
      const term = params.get('searchTerm')?.toLowerCase();
      const categoryId = Number(params.get('categoryId')) || null;
      const items = s.products
        .filter(p => !term || p.name.toLowerCase().includes(term) || p.sku.toLowerCase().includes(term))
        .filter(p => !categoryId || p.categoryId === categoryId)
        .filter(p => params.get('activeOnly') !== 'true' || p.isActive)
        .filter(p => params.get('lowStockOnly') !== 'true' || p.stock <= p.minimumStock)
        .sort((a, b) => a.name.localeCompare(b.name))
        .map(p => this.productView(p));
      return ok(this.page(items, params));
    }

    const product = s.products.find(p => p.id === Number(id));
    if (method === 'GET') return product ? ok(this.productView(product)) : notFound('El producto');

    if (method === 'POST') {
      const sku = String(body.sku ?? '').trim();
      const name = String(body.name ?? '').trim();
      if (sku.length < 3 || name.length < 2 || !(body.price > 0)) return problem(400, 'Error de validación', 'Revisa SKU, nombre y precio (mayor a 0).');
      if (!s.categories.some(c => c.id === body.categoryId)) return problem(404, 'Recurso no encontrado', 'La categoría no existe.');
      if (s.products.some(p => p.sku.toLowerCase() === sku.toLowerCase())) return problem(409, 'Conflicto de negocio', 'Ya existe un producto con ese SKU.');
      const created: Product = {
        id: s.nextId.product++, categoryId: body.categoryId, sku, name, description: body.description || undefined,
        price: body.price, stock: Math.max(0, body.initialStock ?? 0), minimumStock: body.minimumStock ?? 5, isActive: true,
        createdAt: nowIso()
      };
      s.products.push(created);
      if (created.stock > 0) this.movement(created, 'IN', created.stock, 0, 'Stock inicial por alta de producto');
      this.audit('Products', created.id, 'INSERT', null, this.auditFields(created));
      return ok(this.productView(created), 201);
    }

    if (!product) return notFound('El producto');
    if (method === 'PUT') {
      const before = this.auditFields(product);
      Object.assign(product, {
        categoryId: body.categoryId ?? product.categoryId, sku: body.sku ?? product.sku, name: body.name ?? product.name,
        description: body.description ?? product.description, price: body.price ?? product.price,
        minimumStock: body.minimumStock ?? product.minimumStock, isActive: body.isActive ?? product.isActive
      });
      this.audit('Products', product.id, 'UPDATE', before, this.auditFields(product));
      return ok(this.productView(product));
    }

    // DELETE: borrado lógico si hay historial (siempre lo hay en la demo), físico si no.
    const hasHistory = s.movements.some(m => m.productId === product.id);
    if (hasHistory) {
      product.isActive = false;
    } else {
      s.products = s.products.filter(p => p !== product);
    }
    this.audit('Products', product.id, hasHistory ? 'UPDATE' : 'DELETE', { IsActive: true }, { IsActive: false });
    return ok(null, 204);
  }

  // ─── Inventario ───

  private inventory(method: string, id: string | undefined, params: HttpParams, body: any): DemoReply {
    const s = this.state;
    if (id === 'movements' && method === 'GET') {
      const productId = Number(params.get('productId')) || null;
      const items = s.movements.filter(m => !productId || m.productId === productId).sort((a, b) => b.id - a.id);
      return ok(this.page(items, params));
    }

    const product = s.products.find(p => p.id === body.productId);
    if (!product) return notFound('El producto');

    if (id === 'entries') {
      const quantity = Number(body.quantity);
      if (!Number.isInteger(quantity) || quantity < 1) return problem(400, 'Error de validación', 'La cantidad debe ser mayor a 0.');
      const previous = product.stock;
      product.stock += quantity;
      this.audit('Products', product.id, 'UPDATE', { Stock: previous }, { Stock: product.stock });
      return ok(this.movement(product, 'IN', quantity, previous, String(body.reason ?? '')), 201);
    }

    if (id === 'adjustments') {
      const newStock = Number(body.newStock);
      if (!Number.isInteger(newStock) || newStock < 0) return problem(400, 'Error de validación', 'El stock final no puede ser negativo.');
      const previous = product.stock;
      if (newStock === previous) return problem(400, 'Regla de dominio violada', 'El stock final es igual al actual: no hay nada que ajustar.');
      product.stock = newStock;
      this.alertIfLow(product);
      this.audit('Products', product.id, 'UPDATE', { Stock: previous }, { Stock: newStock });
      return ok(this.movement(product, 'ADJUSTMENT', Math.abs(newStock - previous), previous, String(body.reason ?? '')), 201);
    }

    return notFound('El endpoint');
  }

  private movement(product: Product, type: 'IN' | 'OUT' | 'ADJUSTMENT', quantity: number, previousStock: number,
                   reason: string, saleId: number | null = null) {
    const movement = {
      id: this.state.nextId.movement++, productId: product.id, productName: product.name, productSKU: product.sku,
      userId: DEMO_USER.id, userFullName: DEMO_USER.fullName, saleId, movementType: type, quantity, previousStock,
      newStock: product.stock, reason, createdAt: nowIso()
    };
    this.state.movements.push(movement);
    return movement;
  }

  // ─── Ventas ───

  private sales(method: string, id: string | undefined, params: HttpParams, body: any, headers: HttpHeaders): DemoReply {
    const s = this.state;

    if (method === 'GET' && id) {
      const sale = s.sales.find(x => x.id === Number(id));
      return sale ? ok(sale) : notFound('La venta');
    }

    if (method === 'GET') {
      const from = params.get('from') ? Date.parse(`${params.get('from')}Z`) : null;
      const to = params.get('to') ? Date.parse(`${params.get('to')}Z`) : null;
      const items = s.sales
        .filter(x => (from === null || Date.parse(x.saleDate) >= from) && (to === null || Date.parse(x.saleDate) <= to))
        .sort((a, b) => Date.parse(b.saleDate) - Date.parse(a.saleDate));
      return ok(this.page(items, params));
    }

    const key = headers.get('Idempotency-Key');
    if (!key) return problem(400, 'Idempotency-Key requerido', 'Envíe el encabezado Idempotency-Key con un UUID válido.');
    const replay = s.sales.find(x => x.id === s.idempotency[key]);
    if (replay) return ok(replay, 201);

    const items: Array<{ productId: number; quantity: number }> = body.items ?? [];
    if (items.length === 0) return problem(400, 'Error de validación', 'La venta debe contener al menos un producto.');
    if (new Set(items.map(i => i.productId)).size !== items.length) return problem(400, 'Error de validación', 'Hay productos repetidos en la venta.');

    for (const item of items) {
      const product = s.products.find(p => p.id === item.productId);
      if (!product) return notFound('El producto');
      if (!product.isActive || !(item.quantity > 0)) return problem(400, 'Error de validación', `El producto '${product.name}' no puede venderse.`);
      if (product.stock < item.quantity) {
        return problem(400, 'Stock insuficiente', `Stock insuficiente para '${product.name}': disponible ${product.stock}, solicitado ${item.quantity}.`);
      }
    }

    const saleId = s.nextId.sale++;
    const details = items.map((item, index) => {
      const product = s.products.find(p => p.id === item.productId)!;
      const previous = product.stock;
      product.stock -= item.quantity;
      this.movement(product, 'OUT', item.quantity, previous, 'Venta registrada', saleId);
      this.alertIfLow(product);
      return {
        id: index + 1, productId: product.id, productName: product.name, productSKU: product.sku, quantity: item.quantity,
        unitPrice: product.price, subtotal: round(product.price * item.quantity)
      };
    });

    const sale = {
      id: saleId, userId: DEMO_USER.id, userFullName: DEMO_USER.fullName, saleDate: nowIso(),
      total: round(details.reduce((sum, d) => sum + d.subtotal, 0)), details
    };
    s.sales.push(sale);
    s.idempotency[key] = saleId;
    s.outbox.push({
      id: eventIdFor(saleId), eventType: 'SaleCompleted.v1', status: 'Processed', occurredAtUtc: sale.saleDate,
      processedAtUtc: sale.saleDate, retryCount: 0, nextAttemptAtUtc: sale.saleDate, lastError: null, lockedUntilUtc: null,
      leaseExpired: false, payloadBytes: 180 + details.length * 40, saleId, itemCount: details.length,
      consumers: [{ consumer: 'sales-inventory.stock-alerts', processedAtUtc: sale.saleDate }]
    });
    this.audit('Sales', saleId, 'INSERT', null, { Total: sale.total });
    return ok(sale, 201);
  }

  // ─── Notificaciones ───

  private alertIfLow(product: Product): void {
    if (product.stock > product.minimumStock) return;
    const type = product.stock <= 0 ? 'OutOfStock' : 'LowStock';
    const pending = this.state.notifications.some(n => !n.isRead && n.productId === product.id && n.type === type);
    if (!pending) {
      this.state.notifications.push(stockNotification(this.state.nextId.notification++, product, nowIso()));
    }
  }

  private notifications(method: string, id: string | undefined, action: string | undefined, params: HttpParams): DemoReply {
    const list = this.state.notifications;
    if (id === 'unread-count') return ok({ count: list.filter(n => !n.isRead).length });

    if (id === 'read-all') {
      const unread = list.filter(n => !n.isRead);
      unread.forEach(n => { n.isRead = true; n.readAtUtc = nowIso(); });
      return ok({ updated: unread.length });
    }

    if (method === 'POST' && action === 'read') {
      const notification = list.find(n => n.id === Number(id));
      if (!notification) return notFound('La notificación');
      notification.isRead = true;
      notification.readAtUtc = nowIso();
      return ok(null, 204);
    }

    const unread = params.get('unread');
    const items = list
      .filter(n => unread === null || n.isRead === (unread === 'false'))
      .filter(n => !params.get('type') || n.type === params.get('type'))
      .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
    return ok(this.page(items, params));
  }

  // ─── Outbox, auditoría y estado del sistema ───

  private outbox(method: string, id: string | undefined, action: string | undefined, params: HttpParams): DemoReply {
    const list = this.state.outbox;
    if (id === 'summary') {
      const count = (status: string) => list.filter(m => m.status === status).length;
      return ok({ pending: count('Pending'), processing: count('Processing'), processed: count('Processed'), failed: count('Failed'), expiredLeases: 0, oldestPendingAtUtc: null });
    }

    if (id === 'messages' && !action) {
      const status = params.get('status');
      const eventType = params.get('eventType');
      const items = list
        .filter(m => !status || m.status.toLowerCase() === status.toLowerCase())
        .filter(m => !eventType || m.eventType === eventType)
        .sort((a, b) => Date.parse(b.occurredAtUtc) - Date.parse(a.occurredAtUtc))
        .map(({ id: messageId, eventType: type, status: st, occurredAtUtc, processedAtUtc, retryCount, nextAttemptAtUtc, lastError }) =>
          ({ id: messageId, eventType: type, status: st, occurredAtUtc, processedAtUtc, retryCount, nextAttemptAtUtc, lastError }));
      return ok(this.page(items, params));
    }

    const message = list.find(m => m.id === action);
    if (!message) return notFound('El evento');
    if (method === 'POST') return problem(409, 'Conflicto de negocio', 'Solo se pueden reprocesar eventos en estado Failed.');
    return ok(message);
  }

  private auditLogs(id: string | undefined, params: HttpParams): DemoReply {
    const list = this.state.audit;
    if (id) {
      const entry = list.find(a => a.id === Number(id));
      return entry ? ok(entry) : notFound('El registro de auditoría');
    }

    const from = params.get('from') ? Date.parse(`${params.get('from')}Z`) : null;
    const to = params.get('to') ? Date.parse(`${params.get('to')}Z`) : null;
    const items = list
      .filter(a => !params.get('entityName') || a.entityName === params.get('entityName'))
      .filter(a => !params.get('entityId') || a.entityId === params.get('entityId'))
      .filter(a => !params.get('action') || a.action === params.get('action'))
      .filter(a => !params.get('userId') || a.userId === Number(params.get('userId')))
      .filter(a => !params.get('correlationId') || a.correlationId === params.get('correlationId'))
      .filter(a => (from === null || Date.parse(a.occurredAtUtc) >= from) && (to === null || Date.parse(a.occurredAtUtc) <= to))
      .sort((a, b) => b.id - a.id)
      .map(({ oldValues, newValues, ...item }) => item);
    return ok(this.page(items, params));
  }

  /** Instantánea sencilla coherente con los datos de la demo (no hay Worker ni SQL Server reales). */
  private systemHealth(): SystemHealth {
    const s = this.state;
    const dayAgo = Date.now() - 86_400_000;
    const now = nowIso();
    return {
      generatedAtUtc: now,
      status: 'Healthy',
      api: { status: 'Up', version: 'demo' },
      sqlServer: { status: 'Available', latencyMs: null },
      worker: {
        status: 'Active', processing: 'Healthy', heartbeatThresholdSeconds: 45,
        instances: [{
          instanceId: '00000000-0000-4000-8000-000000000001', version: 'demo', startedAtUtc: now, lastSeenAtUtc: now,
          secondsSinceLastSeen: 0, status: 'Active', processing: 'Healthy', lastSuccessfulCycleAtUtc: now, lastErrorType: null
        }]
      },
      events: {
        pending: 0, processing: 0, processed: s.outbox.length, failed: 0, expiredLeases: 0, oldestPendingAtUtc: null,
        oldestPendingAgeSeconds: null,
        processedLast24Hours: s.outbox.filter(m => Date.parse(m.occurredAtUtc) >= dayAgo).length, failedLast24Hours: 0
      },
      seriesStatus: 'InsufficientData',
      series: null,
      warnings: []
    };
  }

  // ─── Auditoría y utilidades ───

  private auditFields(p: Product) {
    return { Sku: p.sku, Name: p.name, Price: p.price, MinimumStock: p.minimumStock, IsActive: p.isActive, Stock: p.stock };
  }

  private audit(entityName: string, entityId: number, action: 'INSERT' | 'UPDATE' | 'DELETE',
                oldValues: Record<string, unknown> | null, newValues: Record<string, unknown> | null): void {
    const keys = Object.keys(newValues ?? oldValues ?? {});
    const changed = action === 'UPDATE'
      ? keys.filter(k => JSON.stringify(oldValues?.[k]) !== JSON.stringify(newValues?.[k]))
      : keys;
    if (changed.length === 0) return;

    const entry: AuditLogDetail = {
      id: this.state.nextId.audit++, entityName, entityId: String(entityId), action, userId: DEMO_USER.id, actorType: 'User',
      source: 'API', changedFields: changed, occurredAtUtc: nowIso(), correlationId: generateUuid().replace(/-/g, ''),
      oldValues, newValues
    };
    this.state.audit.push(entry);
  }

  private page<T>(items: T[], params: HttpParams): PagedResult<T> {
    const pageSize = Math.min(Math.max(Number(params.get('pageSize')) || 20, 1), 1000);
    const totalPages = Math.ceil(items.length / pageSize);
    const pageNumber = Math.max(Number(params.get('pageNumber')) || 1, 1);
    return {
      items: items.slice((pageNumber - 1) * pageSize, pageNumber * pageSize),
      totalItems: items.length, pageNumber, pageSize, totalPages,
      hasPreviousPage: pageNumber > 1, hasNextPage: pageNumber < totalPages
    };
  }

  // ─── Persistencia ───

  private load(): DemoState {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      const parsed = raw ? (JSON.parse(raw) as DemoState) : null;
      if (parsed?.nextId && Array.isArray(parsed.products)) {
        return parsed;
      }
    } catch {
      // Almacenamiento bloqueado o datos corruptos: se parte de los datos de ejemplo.
    }

    return createSeedState();
  }

  private persist(): void {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(this.state));
    } catch {
      // Sin almacenamiento disponible la demo sigue funcionando en memoria durante la sesión.
    }
  }
}
