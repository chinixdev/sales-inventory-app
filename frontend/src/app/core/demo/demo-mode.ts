/**
 * `DEMO_MODE` lo define el compilador (angular.json: `define`). Es `false` salvo en la configuración «demo»
 * (`npm run build:demo`, `npm run start:demo` o `VITE_DEMO_MODE=true npm run build`). En las pruebas no está definido.
 */
declare const DEMO_MODE: boolean | undefined;

export const isDemoMode: boolean = typeof DEMO_MODE !== 'undefined' && DEMO_MODE === true;
