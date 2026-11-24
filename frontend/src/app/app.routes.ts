import { Routes } from '@angular/router';
import { authGuard, roleGuard } from './core/guards/auth.guard';
import { ShellComponent } from './layout/shell.component';

export const routes: Routes = [
  {
    path: 'auth',
    loadChildren: () => import('./features/auth/auth.routes').then(m => m.AUTH_ROUTES)
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    canActivateChild: [authGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        title: 'Dashboard · SalesInventory',
        loadChildren: () => import('./features/dashboard/dashboard.routes').then(m => m.DASHBOARD_ROUTES)
      },
      {
        path: 'products',
        title: 'Productos · SalesInventory',
        loadChildren: () => import('./features/products/products.routes').then(m => m.PRODUCTS_ROUTES)
      },
      {
        path: 'categories',
        title: 'Categorías · SalesInventory',
        loadChildren: () => import('./features/categories/categories.routes').then(m => m.CATEGORIES_ROUTES)
      },
      {
        path: 'inventory',
        title: 'Inventario · SalesInventory',
        loadChildren: () => import('./features/inventory/inventory.routes').then(m => m.INVENTORY_ROUTES)
      },
      {
        path: 'sales',
        title: 'Ventas · SalesInventory',
        loadChildren: () => import('./features/sales/sales.routes').then(m => m.SALES_ROUTES)
      },
      {
        path: 'notifications',
        title: 'Notificaciones · SalesInventory',
        loadChildren: () => import('./features/notifications/notifications.routes').then(m => m.NOTIFICATIONS_ROUTES)
      },
      {
        // Solo Admin: el guard evita la pantalla y la API (rol Admin) rechaza cualquier llamada.
        path: 'admin/events',
        title: 'Monitor de eventos · SalesInventory',
        canActivate: [roleGuard],
        data: { roles: ['Admin'] },
        loadChildren: () => import('./features/admin/events/events.routes').then(m => m.EVENTS_ROUTES)
      },
      {
        path: 'admin/system-health',
        title: 'Estado del sistema · SalesInventory',
        canActivate: [roleGuard],
        data: { roles: ['Admin'] },
        loadChildren: () => import('./features/admin/system-health/system-health.routes').then(m => m.SYSTEM_HEALTH_ROUTES)
      },
      {
        path: 'admin/audit-logs',
        title: 'Auditoría · SalesInventory',
        canActivate: [roleGuard],
        data: { roles: ['Admin'] },
        loadChildren: () => import('./features/admin/audit-logs/audit-logs.routes').then(m => m.AUDIT_LOGS_ROUTES)
      },
      {
        path: 'access-denied',
        title: 'Acceso denegado · SalesInventory',
        loadComponent: () => import('./pages/access-denied.component').then(m => m.AccessDeniedComponent)
      },
      {
        path: '**',
        title: 'No encontrada · SalesInventory',
        loadComponent: () => import('./pages/not-found.component').then(m => m.NotFoundComponent)
      }
    ]
  }
];
