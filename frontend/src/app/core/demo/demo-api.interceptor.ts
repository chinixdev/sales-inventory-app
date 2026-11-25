import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { of, switchMap, throwError, timer } from 'rxjs';
import { environment } from '../../../environments/environment';
import { DemoBackend } from './demo-backend';

/**
 * Solo se registra en el modo demo. Responde a las solicitudes dirigidas a la API con la API simulada (sin red) y deja
 * pasar el resto (por ejemplo, archivos estáticos).
 */
export const demoApiInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith(environment.apiUrl)) {
    return next(request);
  }

  const path = request.url.slice(environment.apiUrl.length).replace(/^\/+/, '');
  const reply = inject(DemoBackend).handle(request.method, path, request.params, request.body, request.headers);

  // Una pequeña latencia simulada hace visibles los estados de carga.
  return timer(120).pipe(
    switchMap(() =>
      reply.status < 400
        ? of(new HttpResponse({ status: reply.status, body: reply.body }))
        : throwError(() => new HttpErrorResponse({ status: reply.status, error: reply.body, url: request.url }))
    )
  );
};
