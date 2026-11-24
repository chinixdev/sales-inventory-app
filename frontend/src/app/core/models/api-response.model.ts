export interface ApiResponse<T> {
  success: boolean;
  message?: string;
  data: T;
  errors?: string[];
}

/**
 * Traduce un error HTTP a un mensaje legible. Prioriza el detalle del backend
 * (ProblemDetails) y los errores de validación (400) antes del título genérico.
 */
export function getApiErrorMessage(error: unknown, fallback: string): string {
  if (typeof error !== 'object' || error === null) {
    return fallback;
  }

  const status = 'status' in error && typeof error.status === 'number' ? error.status : 0;
  const payload = 'error' in error ? error.error : null;
  if (typeof payload === 'object' && payload !== null) {
    if ('detail' in payload && typeof payload.detail === 'string' && payload.detail) {
      return payload.detail;
    }

    if ('errors' in payload && typeof payload.errors === 'object' && payload.errors !== null) {
      const messages = Object.values(payload.errors).flatMap(value =>
        Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
      );
      if (messages.length) {
        return messages.join(' ');
      }
    }

    if ('title' in payload && typeof payload.title === 'string' && payload.title) {
      return payload.title;
    }
  }

  switch (status) {
    case 400:
      return 'Los datos enviados no son válidos. Revisa el formulario.';
    case 401:
      return 'La sesión no es válida o expiró. Inicia sesión nuevamente.';
    case 403:
      return 'Tu usuario no tiene permisos para realizar esta operación.';
    case 404:
      return 'El recurso solicitado no existe.';
    case 409:
      return 'La operación entra en conflicto con el estado actual de los datos.';
    case 0:
      return fallback;
    default:
      return `${fallback} (HTTP ${status}).`;
  }
}
