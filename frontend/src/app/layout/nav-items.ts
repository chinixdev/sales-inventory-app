import { UserRole } from '../core/models/user.model';
import { IconName } from '../shared/components/icon/icon.component';

export interface NavItem {
  label: string;
  path: string;
  icon: IconName;
  /** Si se define, solo estos roles ven la entrada (la API sigue siendo la autoridad real). */
  roles?: readonly UserRole[];
}

export const NAV_ITEMS: readonly NavItem[] = [
  { label: 'Dashboard', path: '/dashboard', icon: 'dashboard' },
  { label: 'Productos', path: '/products', icon: 'products' },
  { label: 'Categorías', path: '/categories', icon: 'categories' },
  { label: 'Inventario', path: '/inventory', icon: 'inventory' },
  { label: 'Ventas', path: '/sales', icon: 'sales' },
  { label: 'Monitor de eventos', path: '/admin/events', icon: 'activity', roles: ['Admin'] },
  { label: 'Estado del sistema', path: '/admin/system-health', icon: 'activity', roles: ['Admin'] },
  { label: 'Auditoría', path: '/admin/audit-logs', icon: 'lock', roles: ['Admin'] }
];
