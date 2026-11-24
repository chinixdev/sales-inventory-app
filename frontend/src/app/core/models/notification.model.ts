export type NotificationType = 'LowStock' | 'OutOfStock';

/** Contrato real de GET /api/notifications. */
export interface AppNotification {
  id: number;
  type: NotificationType;
  productId: number;
  title: string;
  message: string;
  currentStock: number;
  minimumStock: number;
  createdAtUtc: string;
  isRead: boolean;
  readAtUtc: string | null;
}

export interface UnreadCount {
  count: number;
}

export interface MarkedRead {
  updated: number;
}
