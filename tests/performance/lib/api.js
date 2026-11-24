import http from 'k6/http';
import { Counter, Rate, Trend } from 'k6/metrics';
import { config } from './config.js';

// Métricas propias. Etiquetas solo de baja cardinalidad.
export const unexpectedErrors = new Counter('unexpected_errors');
export const salesConfirmed = new Counter('sales_confirmed');
export const conflicts409 = new Counter('conflicts_409');
export const businessRejections = new Counter('business_rejections');
export const conflictRate = new Rate('conflict_rate');
export const saleDuration = new Trend('sale_create_duration', true);
export const idempotencyViolations = new Counter('idempotency_violations');

const JSON_HEADERS = { 'Content-Type': 'application/json' };

/** Genera un UUID v4 (no criptográfico: solo identifica ventas de prueba). */
export function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0;
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

export function authHeaders(token, extra) {
  return Object.assign({}, JSON_HEADERS, { Authorization: `Bearer ${token}` }, extra || {});
}

/**
 * Solicitud con `name` fijo como etiqueta (nunca la URL con ids) y estados esperados declarados: solo lo inesperado
 * cuenta en http_req_failed.
 */
export function request(method, path, token, { body, name, expected = [200, 201], headers } = {}) {
  const params = {
    headers: token ? authHeaders(token, headers) : Object.assign({}, JSON_HEADERS, headers || {}),
    tags: { name: name || `${method} ${path.split('?')[0]}` },
    responseCallback: http.expectedStatuses(...expected)
  };
  const payload = body === undefined ? null : JSON.stringify(body);
  return http.request(method, `${config.baseUrl}${path}`, payload, params);
}

/** Variante para http.batch: mismo formato, ejecución concurrente. */
export function batchEntry(method, path, token, { body, name, expected, headers }) {
  return [
    method,
    `${config.baseUrl}${path}`,
    body === undefined ? null : JSON.stringify(body),
    {
      headers: authHeaders(token, headers),
      tags: { name },
      responseCallback: http.expectedStatuses(...expected)
    }
  ];
}

/** Cuenta como error inesperado un estado fuera de los esperados para ese endpoint (nunca registra cuerpos). */
export function expect(response, statuses) {
  if (!statuses.includes(response.status)) {
    unexpectedErrors.add(1);
    return false;
  }
  return true;
}

export function json(response) {
  try {
    return response.json();
  } catch (_) {
    return null;
  }
}

/** Clasifica la respuesta de una venta: confirmed | conflict | rejection | unexpected. Nunca registra cuerpos ni tokens. */
export function classifySale(response) {
  if (response.status === 201) return 'confirmed';
  if (response.status === 409) return 'conflict';
  if (response.status === 400) {
    const body = json(response);
    // Rechazo de negocio esperado cuando el stock no alcanza; cualquier otro 400 es un error de la prueba.
    if (body && typeof body.title === 'string' && /stock insuficiente/i.test(body.title)) return 'rejection';
  }
  return 'unexpected';
}

export function recordSale(response, outcome) {
  saleDuration.add(response.timings.duration);
  conflictRate.add(outcome === 'conflict');
  switch (outcome) {
    case 'confirmed': salesConfirmed.add(1); break;
    case 'conflict': conflicts409.add(1); break;
    case 'rejection': businessRejections.add(1); break;
    default: unexpectedErrors.add(1);
  }
}

export function login(email, password) {
  const response = request('POST', '/api/auth/login', null, {
    body: { email, password },
    name: 'POST /api/auth/login',
    expected: [200]
  });
  const body = json(response);
  return { response, token: body && body.accessToken ? body.accessToken : null };
}
