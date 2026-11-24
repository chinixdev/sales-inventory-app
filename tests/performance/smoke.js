// Smoke: 1 usuario, 1 iteración. Autenticación, productos, inventario y ventas, más la verificación de integridad.
//   k6 run tests/performance/smoke.js   (ver README en esta carpeta para las variables de entorno)
import { check } from 'k6';
import { classifySale, expect, json, login, recordSale, request, uuid } from './lib/api.js';
import { config } from './lib/config.js';
import { finish, provisionData } from './lib/fixture.js';
import { baseThresholds, summaryOutputs, TREND_STATS } from './lib/summary.js';

export const options = {
  vus: 1,
  iterations: 1,
  summaryTrendStats: TREND_STATS,
  thresholds: Object.assign(baseThresholds(), { 'checks{scope:integrity}': ['rate==1'], checks: ['rate==1'] })
};

export function setup() {
  return provisionData([50]);
}

export default function (data) {
  const productId = data.productIds[0];

  const session = login(config.adminEmail, config.adminPassword);
  check(session.response, { 'login 200 con token': r => r.status === 200 && session.token !== null });

  const products = request('GET', '/api/products?pageNumber=1&pageSize=10', data.token, { name: 'GET /api/products' });
  expect(products, [200]);
  check(products, { 'listado de productos 200': r => r.status === 200 });

  const product = request('GET', `/api/products/${productId}`, data.token, { name: 'GET /api/products/{id}' });
  expect(product, [200]);
  check(product, { 'producto de prueba existe': r => r.status === 200 && json(r).stock === 50 });

  const entry = request('POST', '/api/inventory/entries', data.token, {
    body: { productId, quantity: 5, reason: `PERF-${config.runId} entrada smoke` },
    name: 'POST /api/inventory/entries',
    expected: [201]
  });
  expect(entry, [201]);
  check(entry, { 'entrada de stock 201': r => r.status === 201 });

  const sale = request('POST', '/api/sales', data.token, {
    body: { items: [{ productId, quantity: 2 }] },
    name: 'POST /api/sales',
    expected: [201, 400, 409],
    headers: { 'Idempotency-Key': uuid() }
  });
  const outcome = classifySale(sale);
  recordSale(sale, outcome);
  check(sale, { 'venta confirmada (201)': () => outcome === 'confirmed' });

  const movements = request('GET', `/api/inventory/movements?productId=${productId}&pageSize=20`, data.token, {
    name: 'GET /api/inventory/movements'
  });
  expect(movements, [200]);
  check(movements, { 'movimientos 200': r => r.status === 200 });
}

export function teardown(data) {
  finish(data);
}

export function handleSummary(data) {
  return summaryOutputs('smoke', data);
}
