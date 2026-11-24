import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, vi } from 'vitest';
import { AuthService } from './auth.service';
import { NotificationPollingService } from './notification-polling.service';

function tokenFor(role: string): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '');
  return `${encode({ alg: 'HS256' })}.${encode({ role, nameid: '1', exp: Math.floor(Date.now() / 1000) + 600 })}.sig`;
}

describe('NotificationPollingService', () => {
  let http: HttpTestingController;
  let service: NotificationPollingService;
  const isCount = (request: { url: string }) => request.url.endsWith('/notifications/unread-count');

  beforeEach(() => {
    vi.useFakeTimers();
    sessionStorage.setItem('salesinventory.accessToken', tokenFor('Admin'));
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(NotificationPollingService);
  });

  afterEach(() => {
    service.stop();
    vi.useRealTimers();
    sessionStorage.clear();
  });

  it('loads the unread counter immediately and then on the configured interval', () => {
    service.start(5000);
    http.expectOne(isCount).flush({ count: 3 });
    expect(service.unreadCount()).toBe(3);

    vi.advanceTimersByTime(5000);
    http.expectOne(isCount).flush({ count: 7 });
    expect(service.unreadCount()).toBe(7);

    vi.advanceTimersByTime(5000);
    http.expectOne(isCount).flush({ count: 0 });
    expect(service.unreadCount()).toBe(0);
  });

  it('does not start a second timer when started twice', () => {
    service.start(5000);
    service.start(5000);

    http.expectOne(isCount).flush({ count: 1 });
    vi.advanceTimersByTime(5000);
    http.expectOne(isCount).flush({ count: 1 });
  });

  it('stops requesting after stop() and resets the counter', () => {
    service.start(5000);
    http.expectOne(isCount).flush({ count: 5 });

    service.stop();
    vi.advanceTimersByTime(60000);

    http.expectNone(isCount);
    expect(service.unreadCount()).toBe(0);
    expect(service.isRunning).toBe(false);
  });

  it('stops polling by itself when the session is gone', () => {
    service.start(5000);
    http.expectOne(isCount).flush({ count: 2 });

    TestBed.inject(AuthService).logout();
    vi.advanceTimersByTime(5000);

    http.expectNone(isCount);
    expect(service.isRunning).toBe(false);
  });

  it('keeps the previous counter and flags the error when a request fails', () => {
    service.start(5000);
    http.expectOne(isCount).flush({ count: 4 });

    vi.advanceTimersByTime(5000);
    http.expectOne(isCount).flush({}, { status: 500, statusText: 'Error' });

    expect(service.unreadCount()).toBe(4);
    expect(service.hasError()).toBe(true);
  });

  it('lets the UI update the counter right after marking notifications as read', () => {
    service.setUnreadCount(9);
    expect(service.unreadCount()).toBe(9);
    service.setUnreadCount(-3);
    expect(service.unreadCount()).toBe(0);
  });
});
