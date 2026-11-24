import { UserRole } from './user.model';

export interface SessionUser {
  id: string | null;
  email: string | null;
  role: UserRole | null;
}
