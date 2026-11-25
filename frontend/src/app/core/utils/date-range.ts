/**
 * Hora UTC sin sufijo «Z» del instante indicado (fecha local o Date). La API compara contra columnas datetime2 en UTC,
 * así que los filtros de fecha se envían en ese formato.
 */
export function toUtcWallClock(value: string | Date): string {
  return new Date(value).toISOString().slice(0, 23);
}
