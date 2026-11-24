import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NotificationPollingService } from '../../../core/services/notification-polling.service';
import { IconComponent } from '../icon/icon.component';

/** Campana del topbar: enlaza al centro de notificaciones y muestra el contador de no leídas. */
@Component({
  selector: 'app-notification-bell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent],
  template: `
    <a routerLink="/notifications" class="bell" [attr.aria-label]="label()">
      <app-icon name="bell" />
      @if (count() > 0) {
        <span class="badge" aria-hidden="true">{{ display() }}</span>
      }
    </a>
    <span class="sr-only" role="status" aria-live="polite">{{ liveText() }}</span>`,
  styles: [`
    :host { display: inline-flex; }
    .bell {
      position: relative; display: inline-flex; align-items: center; justify-content: center;
      min-width: 40px; min-height: 40px; border: 1px solid var(--color-border); border-radius: 8px;
      background: #fff; color: var(--color-primary); text-decoration: none;
    }
    .bell:hover { border-color: var(--color-gold); }
    .badge {
      position: absolute; top: -6px; right: -6px; min-width: 20px; height: 20px; padding: 0 5px;
      border-radius: 999px; background: #a32626; color: #fff; font-size: .72rem; font-weight: 700;
      line-height: 20px; text-align: center;
    }
  `]
})
export class NotificationBellComponent {
  private readonly polling = inject(NotificationPollingService);

  protected readonly count = this.polling.unreadCount;
  protected readonly display = computed(() => (this.count() > 99 ? '99+' : String(this.count())));
  protected readonly label = computed(() =>
    this.count() > 0 ? `Notificaciones, ${this.count()} sin leer` : 'Notificaciones, sin pendientes'
  );
  protected readonly liveText = computed(() =>
    this.count() > 0 ? `${this.count()} notificaciones sin leer` : ''
  );
}
