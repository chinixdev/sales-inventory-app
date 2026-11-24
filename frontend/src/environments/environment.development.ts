export const environment = {
  production: false,
  apiUrl: 'http://localhost:5235/api',
  /** Intervalo de actualización del contador de notificaciones sin leer. */
  notificationPollingMs: 15000,
  /** Intervalo de actualización automática del estado del sistema (monitoreo operativo). */
  systemHealthPollingMs: 30000
};
