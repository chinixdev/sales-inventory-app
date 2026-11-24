// Concurrencia:
//  1) oversell: muchas ventas simultáneas sobre un producto con stock LIMITADO. Nunca puede haber más ventas
//     confirmadas que stock (ni stock negativo); 400 «stock insuficiente» y 409 son resultados esperados.
//  2) idempotency: solicitudes simultáneas con la MISMA Idempotency-Key. Todas las respuestas 201 deben devolver la
//     misma venta, y no puede haber más ventas que claves únicas.
// Al terminar se verifica la integridad (stock, movimientos, ventas y eventos Outbox) con la API.
//   OVERSELL_STOCK (20), OVERSELL_ATTEMPTS (200), OVERSELL_VUS (50), IDEM_GROUPS (20), IDEM_PARALLEL (5), IDEM_VUS (10)
import http from 'k6/http';
import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { batchEntry, classifySale, conflictRate, conflicts409, idempotencyViolations, json, unexpectedErrors, uuid } from './lib/api.js';
import { clamp, number } from './lib/config.js';
import { finish, provisionData } from './lib/fixture.js';
import { sell } from './lib/flows.js';
import { baseThresholds, summaryOutputs, TREND_STATS } from './lib/summary.js';

const stock = clamp(Math.round(number('OVERSELL_STOCK', 20)), 1, 1000);
const attempts = clamp(Math.round(number('OVERSELL_ATTEMPTS', 200)), stock + 1, 5000);
const oversellVus = clamp(Math.round(number('OVERSELL_VUS', 50)), 2, 100);
const groups = clamp(Math.round(number('IDEM_GROUPS', 20)), 1, 500);
const parallel = clamp(Math.round(number('IDEM_PARALLEL', 5)), 2, 20);
const idemVus = clamp(Math.round(number('IDEM_VUS', 10)), 1, 50);

const groupsConfirmed = new Counter('idempotent_groups_confirmed');

const thresholds = Object.assign(baseThresholds(), {
  // Se confirman EXACTAMENTE tantas ventas como unidades de stock (las de idempotencia no pasan por este contador).
  'sales_confirmed': [`count==${stock}`],
  idempotency_violations: ['count==0'],
  'checks{scope:integrity}': ['rate==1']
});
// Aquí los conflictos 409 son parte del escenario: se miden, pero no hacen fallar la prueba por sí solos.
delete thresholds.conflict_rate;

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    oversell: {
      executor: 'shared-iterations',
      exec: 'oversell',
      vus: oversellVus,
      iterations: attempts,
      maxDuration: '3m'
    },
    idempotency: {
      executor: 'shared-iterations',
      exec: 'idempotency',
      vus: idemVus,
      iterations: groups,
      maxDuration: '3m'
    }
  },
  thresholds
};

export function setup() {
  // Producto 0: stock limitado. Producto 1: stock amplio para las pruebas de idempotencia.
  const data = provisionData([stock, 1000000]);
  // Esperadas: el producto limitado se vende por completo; cada grupo de idempotencia produce exactamente una venta.
  data.expectedSalesPerProduct = [stock, groups];
  return data;
}

export function oversell(data) {
  // Un 409 (conflicto de concurrencia optimista) es transitorio: se reintenta con la MISMA clave (idempotente), de modo que el
  // resultado final sea determinista: confirmadas == stock y el resto, rechazo «stock insuficiente».
  const key = uuid();
  for (let attempt = 0; attempt < 15; attempt++) {
    if (sell(data, data.productIds[0], 1, key).outcome !== 'conflict') return;
    sleep(0.02 + Math.random() * 0.1);
  }
}

export function idempotency(data) {
  const key = uuid();
  const entry = () => batchEntry('POST', '/api/sales', data.token, {
    body: { items: [{ productId: data.productIds[1], quantity: 1 }] },
    name: 'POST /api/sales (misma Idempotency-Key)',
    expected: [201, 409],
    headers: { 'Idempotency-Key': key }
  });

  const saleIds = new Set();
  // Si todas las solicitudes del grupo terminan en 409, se repite el grupo con la MISMA clave: cada grupo debe confirmar
  // exactamente una venta.
  for (let round = 0; round < 5 && saleIds.size === 0; round++) {
    const responses = http.batch(Array.from({ length: parallel }, entry));
    for (const response of responses) {
      const outcome = classifySale(response);
      if (outcome === 'confirmed') {
        const body = json(response);
        if (body && body.id !== undefined) saleIds.add(body.id);
      } else if (outcome === 'conflict') {
        conflicts409.add(1);
        conflictRate.add(true);
      } else {
        // 400 de negocio no es esperable con stock amplio; cualquier otro estado tampoco.
        unexpectedErrors.add(1);
      }
    }
  }

  if (saleIds.size > 0) groupsConfirmed.add(1);
  // Misma clave => una sola venta: ids distintos significa que se duplicó el efecto.
  if (saleIds.size > 1) idempotencyViolations.add(1);
}

export function teardown(data) {
  finish(data);
}

export function handleSummary(data) {
  return summaryOutputs('concurrency', data);
}
