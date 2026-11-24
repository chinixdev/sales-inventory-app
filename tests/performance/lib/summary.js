import { config } from './config.js';

export const TREND_STATS = ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'];

/** Umbrales INICIALES (configurables por variables de entorno). No son resultados medidos. */
export function baseThresholds({ strict = true } = {}) {
  const t = config.thresholds;
  const thresholds = {
    http_req_duration: [`p(95)<${t.p95}`, `p(99)<${t.p99}`],
    http_req_failed: [{ threshold: `rate<${t.errorRate}`, abortOnFail: strict, delayAbortEval: '30s' }],
    unexpected_errors: ['count==0'],
    conflict_rate: [`rate<${t.conflictRate}`]
  };
  if (t.minRps > 0) thresholds.http_reqs = [`rate>${t.minRps}`];
  return thresholds;
}

function valueOf(metric, key) {
  if (!metric) return undefined;
  const values = metric.values || metric;
  return values[key];
}

function fmt(value, digits = 1) {
  return value === undefined || value === null ? 'n/d' : Number(value).toFixed(digits);
}

/** Resumen de texto breve: p95, p99, throughput, tasa de error, conflictos y duración. Solo métricas, sin tokens ni cuerpos. */
export function textReport(name, data) {
  const m = data.metrics;
  const lines = [
    `== ${name} (run ${config.runId}) ==`,
    `duración de la prueba : ${fmt((data.state && data.state.testRunDurationMs) / 1000, 0)} s`,
    `solicitudes           : ${valueOf(m.http_reqs, 'count')} (${fmt(valueOf(m.http_reqs, 'rate'))}/s)`,
    `latencia p95 / p99    : ${fmt(valueOf(m.http_req_duration, 'p(95)'))} ms / ${fmt(valueOf(m.http_req_duration, 'p(99)'))} ms`,
    `tasa de error         : ${fmt(valueOf(m.http_req_failed, 'rate') * 100, 2)} %  (solo estados inesperados)`,
    `errores inesperados   : ${valueOf(m.unexpected_errors, 'count') || 0}`,
    `ventas confirmadas    : ${valueOf(m.sales_confirmed, 'count') || 0}`,
    `conflictos 409        : ${valueOf(m.conflicts_409, 'count') || 0}`,
    `rechazos de negocio   : ${valueOf(m.business_rejections, 'count') || 0}`,
    `venta p95             : ${fmt(valueOf(m.sale_create_duration, 'p(95)'))} ms`
  ];
  return lines.join('\n') + '\n';
}

/** handleSummary: JSON completo en results/ y resumen de texto en consola y en results/. */
export function summaryOutputs(name, data) {
  const text = textReport(name, data);
  return {
    stdout: text,
    [`results/${name}-${config.runId}.json`]: JSON.stringify(data, null, 2),
    [`results/${name}-${config.runId}.txt`]: text
  };
}
