import { check, fail } from 'k6';
import { Counter } from 'k6/metrics';
import { config } from './config.js';
import { json, login, request } from './api.js';

/** Verificaciones omitidas de forma explícita (p. ej. demasiados eventos): se reportan, nunca se ocultan. */
export const integritySkipped = new Counter('integrity_skipped');

const INTEGRITY = { scope: 'integrity' };
const SALE_EVENT = 'SaleCompleted.v1';

/**
 * setup(): crea datos EXCLUSIVOS de la prueba (categoría y productos con prefijo PERF-<runId>). Nunca toca datos
 * existentes. `stocks` es el stock inicial de cada producto (un número o una lista).
 */
export function provisionData(stocks) {
  const { response, token } = login(config.adminEmail, config.adminPassword);
  if (!token) {
    fail(`No se pudo autenticar (HTTP ${response.status}). Revise ADMIN_EMAIL/ADMIN_PASSWORD del entorno de pruebas.`);
  }

  const prefix = `PERF-${config.runId}`;
  const categoryResponse = request('POST', '/api/categories', token, {
    body: { name: prefix },
    name: 'POST /api/categories (setup)',
    expected: [201]
  });
  const category = json(categoryResponse);
  if (categoryResponse.status !== 201 || !category) {
    fail(`No se pudo crear la categoría de prueba (HTTP ${categoryResponse.status}).`);
  }

  const list = Array.isArray(stocks) ? stocks : Array.from({ length: config.products }, () => stocks);
  const productIds = [];
  list.forEach((stock, index) => {
    const created = request('POST', '/api/products', token, {
      body: {
        categoryId: category.id,
        sku: `${prefix}-${index + 1}`,
        name: `PERF producto ${index + 1}`,
        price: 10,
        initialStock: stock,
        minimumStock: 0
      },
      name: 'POST /api/products (setup)',
      expected: [201]
    });
    const product = json(created);
    if (created.status !== 201 || !product) {
      fail(`No se pudo crear el producto de prueba ${index + 1} (HTTP ${created.status}).`);
    }
    productIds.push(product.id);
  });

  return {
    token,
    categoryId: category.id,
    productIds,
    initialStocks: list,
    // Margen de 5 min para el desfase entre el reloj local y el del servidor al filtrar el Outbox.
    windowStart: new Date(Date.now() - 5 * 60 * 1000).toISOString().slice(0, 23)
  };
}

function allMovements(token, productId) {
  const movements = [];
  for (let page = 1; page <= 1000; page++) {
    const response = request('GET', `/api/inventory/movements?productId=${productId}&pageNumber=${page}&pageSize=100`, token, {
      name: 'GET /api/inventory/movements (verify)'
    });
    const body = json(response);
    if (response.status !== 200 || !body) return null;
    movements.push(...body.items);
    if (!body.hasNextPage) break;
  }
  return movements;
}

function outboxSaleIds(token, windowStart) {
  const saleIds = [];
  let total = 0;
  for (let page = 1; page <= 1000; page++) {
    const response = request(
      'GET',
      `/api/outbox/messages?eventType=${SALE_EVENT}&from=${encodeURIComponent(windowStart)}&pageNumber=${page}&pageSize=100`,
      token,
      { name: 'GET /api/outbox/messages (verify)' });
    const body = json(response);
    if (response.status !== 200 || !body) return { ok: false };
    total = body.totalItems;
    if (total > config.verifyMaxEvents) return { ok: true, skipped: true, total };
    for (const item of body.items) {
      const detail = json(request('GET', `/api/outbox/messages/${item.id}`, token, { name: 'GET /api/outbox/messages/{id} (verify)' }));
      if (detail && detail.saleId !== null && detail.saleId !== undefined) saleIds.push(detail.saleId);
    }
    if (!body.hasNextPage) break;
  }
  return { ok: true, skipped: false, total, saleIds };
}

/**
 * Integridad final (solo con la API): stock no negativo, stock = Σ entradas − Σ salidas, un único movimiento OUT por
 * venta y producto (sin ventas duplicadas) y exactamente un evento Outbox por venta confirmada.
 * Devuelve la lista de violaciones; los `check` con scope=integrity alimentan el umbral `checks{scope:integrity}`.
 */
export function verifyIntegrity(data) {
  const violations = [];
  const verify = (name, ok, detail) => {
    check(null, { [name]: () => ok }, INTEGRITY);
    if (!ok) violations.push(detail ? `${name}: ${detail}` : name);
  };

  const confirmedSales = new Set();
  data.productIds.forEach((productId, index) => {
    const productResponse = request('GET', `/api/products/${productId}`, data.token, { name: 'GET /api/products/{id} (verify)' });
    const product = json(productResponse);
    const movements = allMovements(data.token, productId);
    verify('producto y movimientos consultables', productResponse.status === 200 && product !== null && movements !== null,
      `producto ${productId}`);
    if (!product || !movements) return;

    const entered = movements.filter(m => m.movementType === 'IN').reduce((sum, m) => sum + m.quantity, 0);
    const sold = movements.filter(m => m.movementType === 'OUT').reduce((sum, m) => sum + m.quantity, 0);
    const outs = movements.filter(m => m.movementType === 'OUT');
    const saleIds = outs.map(m => m.saleId);

    verify('stock no negativo', product.stock >= 0, `producto ${productId} stock=${product.stock}`);
    verify('stock = entradas - salidas', product.stock === entered - sold,
      `producto ${productId} stock=${product.stock} entradas=${entered} salidas=${sold}`);
    verify('no se vende más de lo ingresado', sold <= entered, `producto ${productId} salidas=${sold} entradas=${entered}`);
    verify('cada salida pertenece a una venta', saleIds.every(id => id !== null && id !== undefined), `producto ${productId}`);
    verify('una salida por venta (sin ventas duplicadas)', new Set(saleIds).size === saleIds.length, `producto ${productId}`);
    // Ventas exitosas ESPERADAS (no solo «sin stock negativo»): el escenario define cuántas deben confirmarse exactamente.
    const expected = data.expectedSalesPerProduct ? data.expectedSalesPerProduct[index] : undefined;
    if (expected !== undefined && expected !== null) {
      verify('ventas exitosas = esperadas', saleIds.length === expected, `producto ${productId} ventas=${saleIds.length} esperadas=${expected}`);
    }
    saleIds.forEach(id => confirmedSales.add(id));
  });

  const outbox = outboxSaleIds(data.token, data.windowStart);
  verify('Outbox consultable', outbox.ok, 'no se pudo consultar /api/outbox/messages');
  if (outbox.ok && outbox.skipped) {
    integritySkipped.add(1);
    console.warn(`Verificación del Outbox OMITIDA: ${outbox.total} eventos superan PERF_VERIFY_MAX_EVENTS=${config.verifyMaxEvents}.`);
  } else if (outbox.ok) {
    const counts = new Map();
    outbox.saleIds.forEach(id => counts.set(id, (counts.get(id) || 0) + 1));
    const missing = [...confirmedSales].filter(id => !counts.has(id));
    const duplicated = [...confirmedSales].filter(id => (counts.get(id) || 0) > 1);
    verify('un evento Outbox por venta confirmada', missing.length === 0 && duplicated.length === 0,
      `ventas sin evento=${missing.length} con evento duplicado=${duplicated.length}`);
  }

  return { violations, confirmedSales: confirmedSales.size };
}

/** teardown(): verifica y, si falla, termina la ejecución con error (sin ocultar nada). Luego limpia los productos de prueba. */
export function finish(data) {
  const result = verifyIntegrity(data);
  console.log(`Integridad: ${result.confirmedSales} venta(s) con salida de stock, ${result.violations.length} violación(es).`);

  if (config.cleanup) {
    // Baja de los productos de PRUEBA (los creados en setup). Las ventas y movimientos históricos permanecen.
    data.productIds.forEach(id => request('DELETE', `/api/products/${id}`, data.token, {
      name: 'DELETE /api/products/{id} (cleanup)',
      expected: [200, 204, 400, 404, 409]
    }));
  }

  if (result.violations.length > 0) {
    throw new Error(`Integridad violada:\n- ${result.violations.slice(0, 20).join('\n- ')}`);
  }
}
