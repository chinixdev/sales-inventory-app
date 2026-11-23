export type StockMovementType = 'IN' | 'OUT' | 'ADJUSTMENT';

export interface StockMovement {
  id: number;
  productId: number;
  productName?: string;
  productSKU?: string;
  userId: number;
  userFullName?: string;
  saleId?: number | null;
  movementType: StockMovementType;
  quantity: number;
  previousStock: number;
  newStock: number;
  reason: string;
  createdAt: string;
}
