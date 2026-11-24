import { LOCALE_ID } from '@angular/core';
import { registerLocaleData } from '@angular/common';
import localeEsPe from '@angular/common/locales/es-PE';
import { TestBed } from '@angular/core/testing';
import { AppDatePipe } from './app-date.pipe';
import { formatPen } from './pen.pipe';

registerLocaleData(localeEsPe);

describe('formatters', () => {
  it('formats amounts in PEN', () => {
    expect(formatPen(1234.5).replace(/\s/g, ' ')).toBe('S/ 1,234.50');
    expect(formatPen(null)).toBe('—');
  });

  it('treats API dates without zone as UTC', () => {
    TestBed.configureTestingModule({ providers: [{ provide: LOCALE_ID, useValue: 'es-PE' }] });
    const pipe = TestBed.runInInjectionContext(() => new AppDatePipe());
    expect(pipe.transform('2026-01-01T12:00:00', "yyyy-MM-dd'T'HH:mm")).toBe(pipe.transform('2026-01-01T12:00:00Z', "yyyy-MM-dd'T'HH:mm"));
    expect(pipe.transform(null)).toBe('—');
  });
});
