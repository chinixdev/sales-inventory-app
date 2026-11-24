import { Routes } from '@angular/router';
import { guestGuard } from '../../core/guards/auth.guard';
import { LoginComponent } from './components/login/login.component';

export const AUTH_ROUTES: Routes = [
  {
    path: 'login',
    title: 'Iniciar sesión · SalesInventory',
    canActivate: [guestGuard],
    component: LoginComponent
  },
  {
    path: '',
    redirectTo: 'login',
    pathMatch: 'full'
  }
];
