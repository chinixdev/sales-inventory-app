export interface Product {
  id: number;
  categoryId: number;
  categoryName?: string;
  sku: string;
  name: string;
  description?: string;
  price: number;
  stock: number;
  minimumStock: number;
  isActive: boolean;
  createdAt: string;
  isBelowMinimumStock?: boolean;
}
