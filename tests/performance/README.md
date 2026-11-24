# Pruebas de rendimiento (k6)

Scripts: `smoke.js`, `load.js` (10–25 VU), `stress.js` (hasta 100 VU), `concurrency.js` (stock limitado e Idempotency-Key repetida).
Cada ejecución crea sus propios datos (`PERF-<runId>`), verifica integridad al final (stock ≥ 0, stock = entradas − salidas, una salida por venta, un evento Outbox por venta) y da de baja sus productos. Las ventas y movimientos quedan: **usa una base de pruebas desechable**.

## Windows (PowerShell, sin Docker)

```powershell
winget install k6 --source winget          # o: choco install k6

$env:BASE_URL = "http://localhost:5235"    # API de PRUEBAS
$env:ADMIN_EMAIL = "<admin-de-pruebas>"
$env:ADMIN_PASSWORD = "<contraseña>"
$env:PERF_CONFIRM_TEST_ENV = "yes"         # confirma que el destino NO es producción

k6 run tests/performance/smoke.js
k6 run tests/performance/load.js
k6 run tests/performance/concurrency.js
k6 run tests/performance/stress.js         # bajo demanda
```

## Protecciones y configuración

- Se aborta si falta `PERF_CONFIRM_TEST_ENV=yes`, si el host parece de producción o si no es local y no está en `PERF_ALLOWED_HOSTS` (lista separada por comas).
- Umbrales iniciales configurables (no son resultados medidos): `P95_MS` (800), `P99_MS` (1500), `ERROR_RATE` (0.01), `CONFLICT_RATE` (0.05), `MIN_RPS` (0 = sin umbral).
- Carga: `LOAD_VUS`, `LOAD_DURATION`; stress: `STRESS_MAX_VUS`, `STRESS_STEPS`; datos: `PERF_PRODUCTS`, `PERF_INITIAL_STOCK`, `PERF_CLEANUP=false` para conservar los productos.
- Estados esperados (201, 409 por conflicto de concurrencia, 400 «stock insuficiente») se cuentan aparte; cualquier otro es un error inesperado (`unexpected_errors`). Los tokens y contraseñas nunca se imprimen.
- Resultados en `tests/performance/results/` (JSON y texto). CI: workflow manual `performance.yml`.
