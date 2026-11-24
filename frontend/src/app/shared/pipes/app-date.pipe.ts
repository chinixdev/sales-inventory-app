import { LOCALE_ID, Pipe, PipeTransform, inject } from '@angular/core';
import { formatDate } from '@angular/common';

const hasZone = /[zZ]|[+-]\d{2}:?\d{2}$/;

/**
 * Formatea fechas de la API de forma uniforme (dd/MM/yyyy HH:mm, hora local).
 * El backend guarda fechas en UTC y, al leerlas de SQL Server, las serializa sin sufijo «Z»;
 * aquí se interpretan explícitamente como UTC para no desplazarlas.
 */
@Pipe({ name: 'appDate', standalone: true })
export class AppDatePipe implements PipeTransform {
  private readonly locale = inject(LOCALE_ID);

  transform(value: string | null | undefined, format = 'dd/MM/yyyy HH:mm'): string {
    if (!value) {
      return '—';
    }

    try {
      return formatDate(hasZone.test(value) ? value : `${value}Z`, format, this.locale);
    } catch {
      return '—';
    }
  }
}
