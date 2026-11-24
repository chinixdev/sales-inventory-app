import { Component, DestroyRef, ElementRef, HostListener, Injector, afterNextRender, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from '../core/services/auth.service';
import { NotificationPollingService } from '../core/services/notification-polling.service';
import { IconComponent } from '../shared/components/icon/icon.component';
import { NotificationBellComponent } from '../shared/components/notification-bell/notification-bell.component';
import { NAV_ITEMS } from './nav-items';

const MOBILE_BREAKPOINT = 900;

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent, NotificationBellComponent],
  templateUrl: './shell.component.html',
  styleUrls: ['./shell.component.scss']
})
export class ShellComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  private readonly polling = inject(NotificationPollingService);

  /** Entradas visibles según el rol de la sesión (la API sigue validando cada operación). */
  protected readonly navItems = computed(() =>
    NAV_ITEMS.filter(item => !item.roles || this.auth.hasRole(...item.roles))
  );
  protected readonly user = this.auth.currentUser;
  protected readonly menuOpen = signal(false);

  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');
  private readonly mainContent = viewChild<ElementRef<HTMLElement>>('mainContent');
  private readonly sidebar = viewChild<ElementRef<HTMLElement>>('sidebar');
  private navigations = 0;

  constructor() {
    this.router.events
      .pipe(
        filter(event => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => {
        this.menuOpen.set(false);
        // En una SPA el foco debe moverse al contenido nuevo tras navegar (no en la carga inicial).
        if (this.navigations++ > 0) {
          afterNextRender(() => this.mainContent()?.nativeElement.focus({ preventScroll: true }), { injector: this.injector });
        }
      });

    // El contador de notificaciones solo se actualiza con sesión activa: se detiene al cerrar sesión.
    effect(() => {
      if (this.auth.currentUser()) {
        this.polling.start();
      } else {
        this.polling.stop();
      }
    });
    this.destroyRef.onDestroy(() => this.polling.stop());

    // Evita el scroll del fondo mientras el menú móvil está abierto.
    effect(() => {
      document.body.style.overflow = this.menuOpen() ? 'hidden' : '';
    });
    this.destroyRef.onDestroy(() => (document.body.style.overflow = ''));
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.menuOpen()) {
      this.closeMenu(true);
    }
  }

  @HostListener('window:resize')
  protected onResize(): void {
    if (this.menuOpen() && window.innerWidth > MOBILE_BREAKPOINT) {
      this.menuOpen.set(false);
    }
  }

  protected closeMenu(returnFocus = false): void {
    this.menuOpen.set(false);
    if (returnFocus) {
      afterNextRender(() => this.menuButton()?.nativeElement.focus(), { injector: this.injector });
    }
  }

  protected toggleMenu(): void {
    if (this.menuOpen()) {
      this.closeMenu(true);
      return;
    }

    this.menuOpen.set(true);
    afterNextRender(
      () => this.sidebar()?.nativeElement.querySelector<HTMLElement>('a.nav-link')?.focus(),
      { injector: this.injector }
    );
  }

  /** Mantiene el foco dentro del menú móvil abierto. */
  protected trapFocus(event: Event): void {
    const keyEvent = event as KeyboardEvent;
    if (!this.menuOpen() || keyEvent.key !== 'Tab') {
      return;
    }

    const focusable = Array.from(
      this.sidebar()?.nativeElement.querySelectorAll<HTMLElement>('a[href]') ?? []
    );
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (!first || !last) {
      return;
    }

    if (keyEvent.shiftKey && document.activeElement === first) {
      keyEvent.preventDefault();
      last.focus();
    } else if (!keyEvent.shiftKey && document.activeElement === last) {
      keyEvent.preventDefault();
      first.focus();
    }
  }

  protected logout(): void {
    this.auth.logout();
    void this.router.navigateByUrl('/auth/login');
  }
}
