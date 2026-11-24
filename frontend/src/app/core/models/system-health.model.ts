/** Contratos de GET /api/admin/system-health (SystemHealthDto). Las secciones no disponibles llegan como null, nunca como 0. */
export type OverallStatus = 'Healthy' | 'Degraded';
export type ComponentStatus = 'Up' | 'Available' | 'Unavailable';
export type WorkerStatus = 'Active' | 'Inactive' | 'NoInformation' | 'Unavailable';
export type ProcessingStatus = 'Healthy' | 'Degraded' | 'Unknown';
export type SeriesStatus = 'Available' | 'InsufficientData' | 'Unavailable';

export interface ApiHealth {
  status: ComponentStatus;
  version: string;
}

export interface SqlServerHealth {
  status: ComponentStatus;
  latencyMs: number | null;
}

export interface WorkerInstance {
  instanceId: string;
  version: string;
  startedAtUtc: string;
  lastSeenAtUtc: string;
  secondsSinceLastSeen: number;
  status: 'Active' | 'Inactive';
  processing: ProcessingStatus;
  lastSuccessfulCycleAtUtc: string | null;
  lastErrorType: string | null;
}

export interface WorkerHealth {
  status: WorkerStatus;
  processing: ProcessingStatus;
  heartbeatThresholdSeconds: number;
  /** null cuando SQL Server no respondió (no es lo mismo que una lista vacía). */
  instances: WorkerInstance[] | null;
}

export interface OutboxHealth {
  pending: number;
  processing: number;
  processed: number;
  failed: number;
  expiredLeases: number;
  oldestPendingAtUtc: string | null;
  oldestPendingAgeSeconds: number | null;
  processedLast24Hours: number | null;
  failedLast24Hours: number | null;
}

export interface ThroughputPoint {
  bucketStartUtc: string;
  processed: number;
  failed: number;
}

export interface ThroughputSeries {
  bucketMinutes: number;
  points: ThroughputPoint[];
}

export interface SystemHealth {
  generatedAtUtc: string | null;
  status: OverallStatus;
  api: ApiHealth;
  sqlServer: SqlServerHealth;
  worker: WorkerHealth;
  events: OutboxHealth | null;
  seriesStatus: SeriesStatus;
  series: ThroughputSeries | null;
  warnings: string[];
}
