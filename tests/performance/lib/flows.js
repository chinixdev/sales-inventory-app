import { check, sleep } from 'k6';
import { classifySale, expect, login, recordSale, request, uuid } from './api.js';
import { config, number } from './config.js';

const thinkTime = number('THINK_TIME_S', 0.3);
const loginRatio = number('LOGIN_RATIO', 0.05);

function pick(list) {
  return list[Math.floor(Math.random() * list.length)];
}

/** Una venta con Idempotency-Key nueva. Devuelve la clasificación (confirmed | conflict | rejection | unexpected). */
export function sell(data, productId, quantity, key) {
  const response = request('POST', '/api/sales', data.token, {
    body: { items: [{ productId, quantity }] },
    name: 'POST /api/sales',
    expected: [201, 400, 409],
    headers: { 'Idempotency-Key': key || uuid() }
  });
  const outcome = classifySale(response);
  recordSale(response, outcome);
  return { response, outcome };
}

/** Flujo de usuario típico para carga sostenida: consulta, ocasional login y entrada de stock, y una venta. */
export function typicalUser(data) {
  const productId = pick(data.productIds);

  if (Math.random() < loginRatio) {
    const { response } = login(config.adminEmail, config.adminPassword);
    expect(response, [200]);
  }

  const list = request('GET', '/api/products?pageNumber=1&pageSize=20', data.token, { name: 'GET /api/products' });
  expect(list, [200]);
  check(list, { 'listado de productos 200': r => r.status === 200 });

  const detail = request('GET', `/api/products/${productId}`, data.token, { name: 'GET /api/products/{id}' });
  expect(detail, [200]);

  const movements = request('GET', `/api/inventory/movements?productId=${productId}&pageSize=20`, data.token, {
    name: 'GET /api/inventory/movements'
  });
  expect(movements, [200]);

  if (Math.random() < 0.1) {
    const entry = request('POST', '/api/inventory/entries', data.token, {
      body: { productId, quantity: 5, reason: `PERF-${config.runId} reposición` },
      name: 'POST /api/inventory/entries',
      // 409: conflicto de concurrencia optimista sobre el mismo producto (esperado bajo carga).
      expected: [201, 409]
    });
    expect(entry, [201, 409]);
  }

  sell(data, productId, 1 + Math.floor(Math.random() * 3));
  sleep(thinkTime);
}
