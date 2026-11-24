import { ThroughputPoint } from '../../../core/models/system-health.model';

/** Duración legible (p. ej. «10 min 3 s»); null se mantiene null: no se convierte en 0. */
export function formatDuration(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) {
    return null;
  }

  const total = Math.floor(seconds);
  if (total < 60) {
    return `${total} s`;
  }

  const days = Math.floor(total / 86400);
  const hours = Math.floor((total % 86400) / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  if (days > 0) {
    return `${days} d ${hours} h`;
  }

  if (hours > 0) {
    return `${hours} h ${minutes} min`;
  }

  return rest > 0 ? `${minutes} min ${rest} s` : `${minutes} min`;
}

export interface Bar {
  x: number;
  width: number;
  processedY: number;
  processedHeight: number;
  failedY: number;
  failedHeight: number;
  label: string;
  processed: number;
  failed: number;
}

/** Barras apiladas (procesados abajo, fallidos encima) escaladas al máximo real de la serie. */
export function buildBars(points: readonly ThroughputPoint[], width: number, height: number): Bar[] {
  if (points.length === 0) {
    return [];
  }

  const max = Math.max(1, ...points.map(p => p.processed + p.failed));
  const slot = width / points.length;
  const barWidth = Math.max(2, slot * 0.7);

  return points.map((point, index) => {
    const processedHeight = (point.processed / max) * height;
    const failedHeight = (point.failed / max) * height;
    return {
      x: index * slot + (slot - barWidth) / 2,
      width: barWidth,
      processedY: height - processedHeight,
      processedHeight,
      failedY: height - processedHeight - failedHeight,
      failedHeight,
      label: point.bucketStartUtc,
      processed: point.processed,
      failed: point.failed
    };
  });
}
