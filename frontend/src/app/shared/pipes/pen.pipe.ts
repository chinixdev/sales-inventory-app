import { Pipe, PipeTransform } from '@angular/core';

const formatter = new Intl.NumberFormat('es-PE', { style: 'currency', currency: 'PEN' });

/** Formatea importes en soles peruanos (S/). */
export function formatPen(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value) ? formatter.format(value) : '—';
}

@Pipe({ name: 'pen', standalone: true })
export class PenPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return formatPen(value);
  }
}
