export type OutboxStatus = 'Pending' | 'Processing' | 'Processed' | 'Failed';

/** Contrato real de GET /api/outbox/summary. */
export interface OutboxSummary {
  pending: number;
  processing: number;
  processed: number;
  failed: number;
  expiredLeases: number;
  oldestPendingAtUtc: string | null;
}

/** Contrato real de GET /api/outbox/messages. */
export interface OutboxMessageItem {
  id: string;
  eventType: string;
  status: OutboxStatus;
  occurredAtUtc: string;
  processedAtUtc: string | null;
  retryCount: number;
  nextAttemptAtUtc: string;
  lastError: string | null;
}

export interface OutboxConsumerRecord {
  consumer: string;
  processedAtUtc: string;
}

/** Contrato real de GET /api/outbox/messages/{id}: información técnica segura, sin payload. */
export interface OutboxMessageDetail extends OutboxMessageItem {
  lockedUntilUtc: string | null;
  leaseExpired: boolean;
  payloadBytes: number;
  saleId: number | null;
  itemCount: number | null;
  consumers: OutboxConsumerRecord[];
}
