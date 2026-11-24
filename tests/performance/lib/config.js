// Configuración común. TODO viene de variables de entorno: nada de credenciales ni URLs en el repositorio.
//   BASE_URL                 URL de la API de PRUEBAS (p. ej. http://localhost:5235)
//   ADMIN_EMAIL/ADMIN_PASSWORD  credenciales de un Admin del entorno de pruebas
//   PERF_CONFIRM_TEST_ENV    debe ser "yes": confirma que el destino es un entorno de pruebas
//   PERF_ALLOWED_HOSTS       hosts NO locales permitidos (separados por comas); por defecto solo localhost
// Los umbrales son valores INICIALES configurables, no resultados medidos.

function required(name) {
  const value = __ENV[name];
  if (!value) {
    throw new Error(`Falta la variable de entorno ${name}.`);
  }
  return value;
}

function number(name, fallback) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${name} debe ser un número no negativo.`);
  }
  return value;
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}

/** Protección contra ejecución accidental en producción. Se evalúa al cargar el script: aborta antes de enviar nada. */
function guardTarget(baseUrl) {
  const match = /^https?:\/\/([^/:?#]+|\[[^\]]+\])/i.exec(baseUrl);
  if (!match) {
    throw new Error('BASE_URL debe ser una URL http(s) válida.');
  }

  const host = match[1].toLowerCase();
  const loopback = ['localhost', '127.0.0.1', '[::1]'];
  const allowed = (__ENV.PERF_ALLOWED_HOSTS || '').split(',').map(h => h.trim().toLowerCase()).filter(Boolean);

  if (__ENV.PERF_CONFIRM_TEST_ENV !== 'yes') {
    throw new Error('Protección: defina PERF_CONFIRM_TEST_ENV=yes para confirmar que el destino es un entorno de PRUEBAS.');
  }
  if (/prod/i.test(host)) {
    throw new Error('Protección: el host parece de producción; estas pruebas crean ventas y movimientos reales.');
  }
  if (!loopback.includes(host) && !allowed.includes(host)) {
    throw new Error(`Protección: el host '${host}' no es local ni está en PERF_ALLOWED_HOSTS.`);
  }
}

const baseUrl = required('BASE_URL').replace(/\/+$/, '');
guardTarget(baseUrl);

export const config = {
  baseUrl,
  adminEmail: required('ADMIN_EMAIL'),
  adminPassword: required('ADMIN_PASSWORD'),

  /** Identificador único de la ejecución: todo dato creado lleva el prefijo PERF-<runId>. */
  runId: (__ENV.PERF_RUN_ID || `${Date.now().toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`)
    .replace(/[^a-zA-Z0-9]/g, '').slice(0, 12),

  products: clamp(number('PERF_PRODUCTS', 20), 1, 200),
  initialStock: clamp(number('PERF_INITIAL_STOCK', 1000000), 1, 2000000000),
  cleanup: (__ENV.PERF_CLEANUP || 'true') !== 'false',
  /** Máximo de eventos del Outbox cuyo detalle se consulta al verificar (evita una verificación interminable). */
  verifyMaxEvents: number('PERF_VERIFY_MAX_EVENTS', 500),

  thresholds: {
    p95: number('P95_MS', 800),
    p99: number('P99_MS', 1500),
    errorRate: number('ERROR_RATE', 0.01),
    conflictRate: number('CONFLICT_RATE', 0.05),
    minRps: number('MIN_RPS', 0)
  }
};

export { clamp, number };
