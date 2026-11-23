export interface ApiResponse<T> {
  success: boolean;
  message?: string;
  data: T;
  errors?: string[];
}

export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (typeof error !== 'object' || error === null) {
    return fallback;
  }

  const status = 'status' in error && typeof error.status === 'number' ? error.status : 0;
  const payload = 'error' in error ? error.error : null;
  if (typeof payload === 'object' && payload !== null) {
    if ('detail' in payload && typeof payload.detail === 'string') {
      return payload.detail;
    }

    if ('title' in payload && typeof payload.title === 'string') {
      return payload.title;
    }

    if ('errors' in payload && typeof payload.errors === 'object' && payload.errors !== null) {
      const messages = Object.values(payload.errors).flatMap(value =>
        Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
      );
      if (messages.length) {
        return messages.join(' ');
      }
    }
  }

  if (status === 403) {
    return 'Tu usuario no tiene permisos para esta operación (HTTP 403).';
  }

  if (status === 401) {
    return 'La sesión no es válida o expiró (HTTP 401).';
  }

  if (status > 0) {
    return `La solicitud falló (HTTP ${status}).`;
  }

  return fallback;
}
