import { inject, Injectable, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from './auth.service';
import { NotificationApiService } from './notification-api.service';

/**
 * Contador de notificaciones sin leer con actualización periódica configurable
 * (`environment.notificationPollingMs`). Se detiene al cerrar sesión o al caducar el token.
 */
@Injectable({ providedIn: 'root' })
export class NotificationPollingService {
  private readonly api = inject(NotificationApiService);
  private readonly auth = inject(AuthService);

  readonly unreadCount = signal(0);
  readonly hasError = signal(false);

  private timer: ReturnType<typeof setInterval> | null = null;
  private request: Subscription | null = null;

  get isRunning(): boolean {
    return this.timer !== null;
  }

  start(intervalMs: number = environment.notificationPollingMs): void {
    if (this.timer !== null) {
      return;
    }

    this.refresh();
    this.timer = setInterval(() => this.refresh(), Math.max(intervalMs, 1000));
  }

  stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }

    this.request?.unsubscribe();
    this.request = null;
    this.unreadCount.set(0);
    this.hasError.set(false);
  }

  refresh(): void {
    // Sin sesión válida no se consulta (y se detiene el sondeo).
    if (!this.auth.isAuthenticated()) {
      this.stop();
      return;
    }

    this.request?.unsubscribe();
    this.request = this.api.getUnreadCount().subscribe({
      next: result => {
        this.unreadCount.set(result.count);
        this.hasError.set(false);
      },
      error: () => this.hasError.set(true)
    });
  }

  /** Actualiza el contador de inmediato tras marcar notificaciones como leídas. */
  setUnreadCount(count: number): void {
    this.unreadCount.set(Math.max(0, count));
  }
}
