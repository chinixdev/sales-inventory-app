// Stress: incremento escalonado hasta STRESS_MAX_VUS (por defecto 50; máximo 100) para encontrar el punto de degradación.
//   STRESS_MAX_VUS, STRESS_STEPS (por defecto 5), STEP_HOLD (por defecto 1m), STEP_RAMP (por defecto 30s)
// NO se ejecuta en cada push: solo bajo demanda (ver .github/workflows/performance.yml).
import { clamp, config, number } from './lib/config.js';
import { finish, provisionData } from './lib/fixture.js';
import { typicalUser } from './lib/flows.js';
import { baseThresholds, summaryOutputs, TREND_STATS } from './lib/summary.js';

const maxVus = clamp(number('STRESS_MAX_VUS', 50), 2, 100);
const steps = clamp(Math.round(number('STRESS_STEPS', 5)), 1, 20);

const stages = [];
for (let step = 1; step <= steps; step++) {
  stages.push({ duration: __ENV.STEP_RAMP || '30s', target: Math.max(1, Math.round((maxVus * step) / steps)) });
  stages.push({ duration: __ENV.STEP_HOLD || '1m', target: Math.max(1, Math.round((maxVus * step) / steps)) });
}
stages.push({ duration: '30s', target: 0 });

export const options = {
  summaryTrendStats: TREND_STATS,
  scenarios: {
    stress: { executor: 'ramping-vus', startVUs: 1, stages, gracefulRampDown: '30s' }
  },
  // En stress el objetivo es observar la degradación: los umbrales NO abortan la ejecución, pero siguen marcándola.
  thresholds: Object.assign(baseThresholds({ strict: false }), { 'checks{scope:integrity}': ['rate==1'] })
};

export function setup() {
  return provisionData(config.initialStock);
}

export default function (data) {
  typicalUser(data);
}

export function teardown(data) {
  finish(data);
}

export function handleSummary(data) {
  return summaryOutputs('stress', data);
}
