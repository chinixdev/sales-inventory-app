import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiService } from './api.service';

export interface ApiHealth {
  status: string;
  service: string;
  environment: string;
  timestampUtc: string;
  version: string;
}

@Injectable({ providedIn: 'root' })
export class HealthApiService {
  private readonly api = inject(ApiService);

  getStatus(): Observable<ApiHealth> {
    return this.api.get<ApiHealth>('health');
  }
}
