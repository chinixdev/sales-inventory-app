import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../core/services/auth.service';
import { NotificationPollingService } from '../core/services/notification-polling.service';
import { ShellComponent } from './shell.component';

function tokenFor(role: string): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  return `${encode({ alg: 'HS256' })}.${encode({ role, email: 'a@b.pe', nameid: '1', exp: Math.floor(Date.now() / 1000) + 600 })}.sig`;
}

describe('ShellComponent', () => {
  function setup(role = 'Admin', unread = 0) {
    sessionStorage.setItem('salesinventory.accessToken', tokenFor(role));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()]
    });
    const fixture = TestBed.createComponent(ShellComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    // El shell arranca el contador de notificaciones al iniciar con sesión.
    http.expectOne(request => request.url.endsWith('/notifications/unread-count')).flush({ count: unread });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const toggle = element.querySelector('.menu-btn') as HTMLButtonElement;
    return { fixture, element, toggle, http };
  }

  afterEach(() => {
    sessionStorage.clear();
    document.body.style.overflow = '';
  });

  describe('navigation and permissions', () => {
    it('shows the Admin-only event monitor entry to Admin', () => {
      const { element } = setup('Admin');
      const labels = Array.from(element.querySelectorAll('a.nav-link')).map(link => link.textContent?.trim());
      expect(labels).toEqual(['Dashboard', 'Productos', 'Categorías', 'Inventario', 'Ventas', 'Monitor de eventos', 'Estado del sistema', 'Auditoría']);
      element.remove();
    });

    it('hides the event monitor entry from Seller', () => {
      const { element } = setup('Seller');
      const labels = Array.from(element.querySelectorAll('a.nav-link')).map(link => link.textContent?.trim());
      expect(labels).toEqual(['Dashboard', 'Productos', 'Categorías', 'Inventario', 'Ventas']);
      element.remove();
    });
  });

  describe('notification bell', () => {
    it('shows the unread counter returned by the API', () => {
      const { element } = setup('Seller', 4);
      const bell = element.querySelector('a.bell') as HTMLAnchorElement;
      expect(bell.querySelector('.badge')?.textContent?.trim()).toBe('4');
      expect(bell.getAttribute('aria-label')).toBe('Notificaciones, 4 sin leer');
      element.remove();
    });

    it('caps the visible counter at 99+', () => {
      const { element } = setup('Admin', 250);
      expect(element.querySelector('a.bell .badge')?.textContent?.trim()).toBe('99+');
      element.remove();
    });

    it('shows no badge when there is nothing unread', () => {
      const { element } = setup('Admin', 0);
      expect(element.querySelector('a.bell .badge')).toBeNull();
      expect(element.querySelector('a.bell')?.getAttribute('aria-label')).toBe('Notificaciones, sin pendientes');
      element.remove();
    });

    it('stops polling when the session is closed', () => {
      const { fixture, element } = setup('Admin', 2);
      const polling = TestBed.inject(NotificationPollingService);
      expect(polling.isRunning).toBe(true);

      TestBed.inject(AuthService).logout();
      fixture.detectChanges();
      TestBed.flushEffects();

      expect(polling.isRunning).toBe(false);
      expect(polling.unreadCount()).toBe(0);
      element.remove();
    });
  });

  describe('mobile menu', () => {
    it('opens the menu, moves focus into it and exposes aria-expanded', async () => {
      const { fixture, element, toggle } = setup();
      expect(toggle.getAttribute('aria-expanded')).toBe('false');

      toggle.click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(element.querySelector('.shell')?.classList.contains('menu-open')).toBe(true);
      expect(element.querySelector('.backdrop')).not.toBeNull();
      expect(document.activeElement?.classList.contains('nav-link')).toBe(true);
      expect(document.body.style.overflow).toBe('hidden');
      element.remove();
    });

    it('closes with Escape, returns focus to the toggle and releases the page scroll', async () => {
      const { fixture, element, toggle } = setup();
      toggle.click();
      fixture.detectChanges();
      await fixture.whenStable();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      await fixture.whenStable();

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(element.querySelector('.backdrop')).toBeNull();
      expect(document.activeElement).toBe(toggle);
      expect(document.body.style.overflow).toBe('');
      element.remove();
    });
  });
});
