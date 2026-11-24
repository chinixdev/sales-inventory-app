// Load: carga sostenida de 10-25 usuarios virtuales (LOAD_VUS, por defecto 10; máximo 25).
//   LOAD_VUS, LOAD_DURATION (p. ej. 5m), RAMP (p. ej. 1m), PERF_PRODUCTS, THINK_TIME_S, LOGIN_RATIO
import { clamp, config, number } from './lib/config.js';
import { finish, provisionData } from './lib/fixture.js';
import { typicalUser } from './lib/flows.js';
import { baseThresholds, summaryOutputs, TREND_STATS } from './lib/summary.js';

const vus = clamp(number('LOAD_VUS', 10), 1, 25);
const hold = __ENV.LOAD_DURATION || '5m';
const ramp = __ENV.RAMP || '1m';

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    sustained_load: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: ramp, target: vus },
        { duration: hold, target: vus },
        { duration: '30s', target: 0 }
      ],
      gracefulRampDown: '30s'
    }
  },
  thresholds: Object.assign(baseThresholds(), { 'checks{scope:integrity}': ['rate==1'] })
};

export function setup() {
  // Mucho stock inicial: la carga sostenida no debe agotar productos (eso se prueba en concurrency.js).
  return provisionData(config.initialStock);
}

export default function (data) {
  typicalUser(data);
}

export function teardown(data) {
  finish(data);
}

export function handleSummary(data) {
  return summaryOutputs('load', data);
}
