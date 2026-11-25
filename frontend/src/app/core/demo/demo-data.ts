import { AuditLogDetail } from '../models/audit-log.model';
import { AppNotification } from '../models/notification.model';
import { OutboxMessageDetail } from '../models/outbox.model';
import { Category } from '../../features/categories/models/category.model';
import { StockMovement } from '../../features/inventory/models/stock-movement.model';
import { Product } from '../../features/products/models/product.model';
import { Sale } from '../../features/sales/models/sale.model';

/**
 * Usuario del modo demo. Son datos públicos y ficticios: no existe ninguna cuenta real detrás y no autentican contra
 * ninguna API.
 */
export const DEMO_USER = {
  id: 1,
  fullName: 'Usuario Demo',
  email: 'demo@salesinventory.demo',
  password: 'demo-1234'
};

export interface DemoState {
  nextId: { category: number; product: number; sale: number; movement: number; notification: number; audit: number };
  categories: Category[];
  products: Product[];
  sales: Sale[];
  movements: StockMovement[];
  notifications: AppNotification[];
  outbox: OutboxMessageDetail[];
  audit: AuditLogDetail[];
  /** Idempotency-Key -> id de la venta: repetir la misma clave devuelve la venta original. */
  idempotency: Record<string, number>;
}

const CATALOG = [
  ['Computación', 'DEMO-TEC-001', 'Teclado mecánico', 189.9, 60, 10],
  ['Computación', 'DEMO-MOU-002', 'Mouse inalámbrico', 79.9, 80, 15],
  ['Computación', 'DEMO-MON-003', 'Monitor 24 pulgadas', 649, 25, 5],
  ['Computación', 'DEMO-LAP-004', 'Laptop 14 pulgadas', 2899, 12, 3],
  ['Accesorios', 'DEMO-CAB-005', 'Cable HDMI 2 m', 24.9, 120, 20],
  ['Accesorios', 'DEMO-USB-006', 'Memoria USB 64 GB', 39.9, 100, 20],
  ['Accesorios', 'DEMO-AUD-007', 'Audífonos con micrófono', 129, 45, 10],
  ['Accesorios', 'DEMO-WEB-008', 'Cámara web HD', 159, 30, 8],
  ['Oficina', 'DEMO-SIL-009', 'Silla ergonómica', 549, 14, 4],
  ['Oficina', 'DEMO-LAM-010', 'Lámpara de escritorio', 69.9, 35, 8],
  ['Oficina', 'DEMO-PAP-011', 'Resma de papel A4', 18.5, 200, 40],
  // Pocas unidades a propósito: tras las ventas quedan bajo el mínimo y generan alertas.
  ['Oficina', 'DEMO-IMP-012', 'Impresora multifunción', 899, 9, 6],
  ['Accesorios', 'DEMO-BAT-013', 'Batería externa 10000 mAh', 89.9, 14, 10]
] as const;

/** Generador pseudoaleatorio determinista (mulberry32): el mismo estado inicial en cada restablecimiento. */
function seededRandom(seed: number): () => number {
  let state = seed;
  return () => {
    state = (state + 0x6d2b79f5) | 0;
    let t = Math.imul(state ^ (state >>> 15), 1 | state);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function eventIdFor(saleId: number): string {
  return `00000000-0000-4000-8000-${String(saleId).padStart(12, '0')}`;
}

export function createSeedState(now: Date = new Date()): DemoState {
  const day = 86_400_000;
  const random = seededRandom(42);
  const iso = (time: number) => new Date(time).toISOString();

  const state: DemoState = {
    nextId: { category: 1, product: 1, sale: 1, movement: 1, notification: 1, audit: 1 },
    categories: [], products: [], sales: [], movements: [], notifications: [], outbox: [], audit: [], idempotency: {}
  };

  const categoryIds = new Map<string, number>();
  for (const [categoryName] of CATALOG) {
    if (!categoryIds.has(categoryName)) {
      const id = state.nextId.category++;
      categoryIds.set(categoryName, id);
      state.categories.push({ id, name: categoryName, isActive: true });
    }
  }

  for (const [categoryName, sku, name, price, stock, minimumStock] of CATALOG) {
    const product: Product = {
      id: state.nextId.product++, categoryId: categoryIds.get(categoryName)!, categoryName, sku, name, price,
      stock, minimumStock, isActive: true, createdAt: iso(now.getTime() - 20 * day)
    };
    state.products.push(product);
    state.movements.push({
      id: state.nextId.movement++, productId: product.id, productName: name, productSKU: sku, userId: DEMO_USER.id,
      userFullName: DEMO_USER.fullName, saleId: null, movementType: 'IN', quantity: stock, previousStock: 0,
      newStock: stock, reason: 'Stock inicial por alta de producto', createdAt: product.createdAt
    });
  }

  for (let i = 0; i < 30; i++) {
    const time = now.getTime() - 14 * day + (i * 14 * day) / 30 + Math.floor(random() * 600) * 60_000;
    const saleId = state.nextId.sale;
    const details: Sale['details'] = [];
    const picks = [...state.products].sort(() => random() - 0.5).slice(0, 1 + Math.floor(random() * 3));

    for (const product of picks) {
      const quantity = 1 + Math.floor(random() * 3);
      if (product.stock < quantity) continue;
      const previousStock = product.stock;
      product.stock -= quantity;
      details.push({
        id: details.length + 1, productId: product.id, productName: product.name, productSKU: product.sku, quantity,
        unitPrice: product.price, subtotal: Math.round(product.price * quantity * 100) / 100
      });
      state.movements.push({
        id: state.nextId.movement++, productId: product.id, productName: product.name, productSKU: product.sku,
        userId: DEMO_USER.id, userFullName: DEMO_USER.fullName, saleId, movementType: 'OUT', quantity, previousStock,
        newStock: product.stock, reason: 'Venta registrada', createdAt: iso(time)
      });
    }

    if (details.length === 0) continue;
    state.nextId.sale++;
    state.sales.push({
      id: saleId, userId: DEMO_USER.id, userFullName: DEMO_USER.fullName, saleDate: iso(time),
      total: Math.round(details.reduce((sum, d) => sum + d.subtotal, 0) * 100) / 100, details
    });
    state.outbox.push({
      id: eventIdFor(saleId), eventType: 'SaleCompleted.v1', status: 'Processed', occurredAtUtc: iso(time),
      processedAtUtc: iso(time + 2000), retryCount: 0, nextAttemptAtUtc: iso(time), lastError: null,
      lockedUntilUtc: null, leaseExpired: false, payloadBytes: 180 + details.length * 40, saleId,
      itemCount: details.length, consumers: [
        { consumer: 'sales-inventory.stock-alerts', processedAtUtc: iso(time + 2000) },
        { consumer: 'sales-inventory.sale-completed', processedAtUtc: iso(time + 2000) }
      ]
    });
  }

  for (const product of state.products.filter(p => p.stock <= p.minimumStock)) {
    state.notifications.push(stockNotification(state.nextId.notification++, product, iso(now.getTime() - day)));
  }

  return state;
}

export function stockNotification(id: number, product: Product, createdAtUtc: string): AppNotification {
  const outOfStock = product.stock <= 0;
  return {
    id, type: outOfStock ? 'OutOfStock' : 'LowStock', productId: product.id,
    title: outOfStock ? 'Producto agotado' : 'Stock bajo',
    message: outOfStock
      ? `${product.name} (${product.sku}) se quedó sin existencias.`
      : `${product.name} (${product.sku}) tiene ${product.stock} unidad(es); el mínimo configurado es ${product.minimumStock}.`,
    currentStock: product.stock, minimumStock: product.minimumStock, createdAtUtc, isRead: false, readAtUtc: null
  };
}
