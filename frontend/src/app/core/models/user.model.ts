export type UserRole = 'Admin' | 'Seller';

export interface User {
  id: number;
  fullName: string;
  email: string;
  role: UserRole;
  isActive: boolean;
  createdAt: string;
}
