export interface SaleDetail {
  id: number;
  productId: number;
  productName?: string;
  productSKU?: string;
  quantity: number;
  unitPrice: number;
  subtotal: number;
}

export interface Sale {
  id: number;
  userId: number;
  userFullName?: string;
  saleDate: string;
  total: number;
  details: SaleDetail[];
}
