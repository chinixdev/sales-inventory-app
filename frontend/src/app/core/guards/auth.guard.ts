import { inject } from '@angular/core';
import { CanActivateFn, CanActivateChildFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { UserRole } from '../models/user.model';

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return auth.isAuthenticated()
    ? true
    : router.createUrlTree(['/auth/login'], { queryParams: { returnUrl: state.url } });
};

/** Evita mostrar el login a una sesión ya iniciada. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.isAuthenticated() ? router.createUrlTree(['/dashboard']) : true;
};

/** Restringe una ruta a los roles indicados en `data.roles`. La API sigue siendo la autoridad real. */
export const roleGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const roles = (route.data['roles'] as UserRole[] | undefined) ?? [];
  return roles.length === 0 || auth.hasRole(...roles) ? true : router.createUrlTree(['/access-denied']);
};

export const authChildGuard: CanActivateChildFn = (route, state) => authGuard(route, state);
