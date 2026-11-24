/** Contratos de GET /api/audit-logs y GET /api/audit-logs/{id} (AuditLogListItemDto / AuditLogDetailDto). */
export type AuditAction = 'INSERT' | 'UPDATE' | 'DELETE';
export type AuditActorType = 'User' | 'System';
export type AuditSource = 'API' | 'Worker';

/** Entidades que el backend permite filtrar (lista blanca de auditoría). */
export const AUDIT_ENTITIES = ['Products', 'Categories', 'Sales', 'SaleDetails', 'StockMovements', 'Users'] as const;
export const AUDIT_ACTIONS: readonly AuditAction[] = ['INSERT', 'UPDATE', 'DELETE'];

export interface AuditLogItem {
  id: number;
  entityName: string;
  entityId: string;
  action: AuditAction;
  userId: number | null;
  actorType: AuditActorType;
  source: AuditSource;
  changedFields: string[];
  occurredAtUtc: string;
  correlationId: string;
}

export interface AuditLogDetail extends AuditLogItem {
  oldValues: unknown;
  newValues: unknown;
}
