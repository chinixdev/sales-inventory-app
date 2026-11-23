import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full'
  },
  {
    path: 'dashboard',
    canActivate: [authGuard],
    loadChildren: () => import('./features/dashboard/dashboard.routes').then(m => m.DASHBOARD_ROUTES)
  },
  {
    path: 'products',
    canActivate: [authGuard],
    loadChildren: () => import('./features/products/products.routes').then(m => m.PRODUCTS_ROUTES)
  },
  {
    path: 'categories',
    canActivate: [authGuard],
    loadChildren: () => import('./features/categories/categories.routes').then(m => m.CATEGORIES_ROUTES)
  },
  {
    path: 'sales',
    canActivate: [authGuard],
    loadChildren: () => import('./features/sales/sales.routes').then(m => m.SALES_ROUTES)
  },
  {
    path: 'inventory',
    canActivate: [authGuard],
    loadChildren: () => import('./features/inventory/inventory.routes').then(m => m.INVENTORY_ROUTES)
  },
  {
    path: 'auth',
    loadChildren: () => import('./features/auth/auth.routes').then(m => m.AUTH_ROUTES)
  },
  {
    path: '**',
    redirectTo: 'dashboard'
  }
];
