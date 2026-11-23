import { inject, Injectable } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { ApiService } from './api.service';
import { LoginRequest, LoginResponse } from '../models/auth.model';

const accessTokenKey = 'salesinventory.accessToken';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);

  login(credentials: LoginRequest): Observable<LoginResponse> {
    return this.api.post<LoginResponse>('auth/login', credentials).pipe(
      tap(response => this.storage?.setItem(accessTokenKey, response.accessToken))
    );
  }

  getAccessToken(): string | null {
    const token = this.storage?.getItem(accessTokenKey) ?? null;
    if (token && this.isTokenExpired(token)) {
      this.logout();
      return null;
    }

    return token;
  }

  isAuthenticated(): boolean {
    return this.getAccessToken() !== null;
  }

  logout(): void {
    this.storage?.removeItem(accessTokenKey);
  }

  private get storage(): Storage | undefined {
    return typeof window === 'undefined' ? undefined : window.sessionStorage;
  }

  private isTokenExpired(token: string): boolean {
    try {
      const payload = token.split('.')[1];
      if (!payload) {
        return true;
      }

      const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
      const expiration = (JSON.parse(atob(base64)) as { exp?: number }).exp;
      return typeof expiration !== 'number' || expiration * 1000 <= Date.now();
    } catch {
      return true;
    }
  }
}
