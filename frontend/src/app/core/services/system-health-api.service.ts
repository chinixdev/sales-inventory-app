import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SystemHealth } from '../models/system-health.model';
import { ApiService } from './api.service';

/** Centro de monitoreo operativo (endpoint exclusivo de Admin, solo lectura). */
@Injectable({ providedIn: 'root' })
export class SystemHealthApiService {
  private readonly api = inject(ApiService);

  get(): Observable<SystemHealth> {
    return this.api.get<SystemHealth>('admin/system-health');
  }
}
