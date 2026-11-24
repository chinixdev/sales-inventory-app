import { computed, inject, Injectable, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { ApiService } from './api.service';
import { LoginRequest, LoginResponse } from '../models/auth.model';
import { SessionUser } from '../models/session.model';
import { UserRole } from '../models/user.model';

const accessTokenKey = 'salesinventory.accessToken';
const roleClaims = ['role', 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
const idClaims = ['nameid', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
const emailClaims = ['email', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress'];

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);
  private readonly token = signal<string | null>(this.readStoredToken());

  /** Usuario de la sesión, derivado únicamente de los claims del JWT. */
  readonly currentUser = computed<SessionUser | null>(() => {
    const token = this.token();
    const payload = token ? this.decodePayload(token) : null;
    if (!payload) {
      return null;
    }

    const role = this.firstString(payload, roleClaims);
    return {
      id: this.firstString(payload, idClaims),
      email: this.firstString(payload, emailClaims),
      role: role === 'Admin' || role === 'Seller' ? role : null
    };
  });

  login(credentials: LoginRequest): Observable<LoginResponse> {
    return this.api.post<LoginResponse>('auth/login', credentials).pipe(
      tap(response => {
        this.storage?.setItem(accessTokenKey, response.accessToken);
        this.token.set(response.accessToken);
      })
    );
  }

  getAccessToken(): string | null {
    const token = this.token();
    if (token && this.isTokenExpired(token)) {
      this.logout();
      return null;
    }

    return token;
  }

  isAuthenticated(): boolean {
    return this.getAccessToken() !== null;
  }

  hasRole(...roles: UserRole[]): boolean {
    const role = this.currentUser()?.role;
    return this.isAuthenticated() && role !== null && role !== undefined && roles.includes(role);
  }

  logout(): void {
    this.storage?.removeItem(accessTokenKey);
    this.token.set(null);
  }

  private readStoredToken(): string | null {
    try {
      return this.storage?.getItem(accessTokenKey) ?? null;
    } catch {
      return null;
    }
  }

  private get storage(): Storage | undefined {
    return typeof window === 'undefined' ? undefined : window.sessionStorage;
  }

  private decodePayload(token: string): Record<string, unknown> | null {
    try {
      const segment = token.split('.')[1];
      if (!segment) {
        return null;
      }

      const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
      const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
      const bytes = Uint8Array.from(atob(padded), char => char.charCodeAt(0));
      return JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
    } catch {
      return null;
    }
  }

  private firstString(payload: Record<string, unknown>, names: string[]): string | null {
    for (const name of names) {
      const value = payload[name];
      if (typeof value === 'string') {
        return value;
      }
    }

    return null;
  }

  private isTokenExpired(token: string): boolean {
    const expiration = this.decodePayload(token)?.['exp'];
    return typeof expiration !== 'number' || expiration * 1000 <= Date.now();
  }
}
