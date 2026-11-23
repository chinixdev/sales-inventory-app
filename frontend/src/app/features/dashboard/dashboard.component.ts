import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CategoryApiService } from '../../core/services/category-api.service';
import { ProductApiService } from '../../core/services/product-api.service';
import { SalesApiService } from '../../core/services/sales-api.service';
import { HealthApiService } from '../../core/services/health-api.service';
import { getApiErrorMessage } from '../../core/models/api-response.model';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
  private readonly categoryApi = inject(CategoryApiService);
  private readonly productApi = inject(ProductApiService);
  private readonly salesApi = inject(SalesApiService);
  private readonly healthApi = inject(HealthApiService);

  systemStats = [
    { label: 'Productos Registrados', value: '—', link: '/products', icon: '📦' },
    { label: 'Categorías Activas', value: '—', link: '/categories', icon: '🏷️' },
    { label: 'Ventas Registradas', value: '—', link: '/sales', icon: '💰' },
    { label: 'Alertas de Stock Mínimo', value: '—', link: '/products', icon: '⚠️' }
  ];
  apiStatus = 'Comprobando API…';
  errorMessage = '';

  ngOnInit(): void {
    this.categoryApi.getAll(true).subscribe({
      next: categories => this.setStat('Categorías Activas', String(categories.length)),
      error: error => this.recordError(error)
    });

    this.productApi.getPaged({ pageNumber: 1, pageSize: 1 }).subscribe({
      next: result => this.setStat('Productos Registrados', String(result.totalItems)),
      error: error => this.recordError(error)
    });

    this.productApi.getPaged({ pageNumber: 1, pageSize: 1, lowStockOnly: true }).subscribe({
      next: result => this.setStat('Alertas de Stock Mínimo', String(result.totalItems)),
      error: error => this.recordError(error)
    });

    this.salesApi.getPaged({ pageNumber: 1, pageSize: 1 }).subscribe({
      next: result => this.setStat('Ventas Registradas', String(result.totalItems)),
      error: error => this.recordError(error)
    });

    this.healthApi.getStatus().subscribe({
      next: health => this.apiStatus = `${health.service}: ${health.status}`,
      error: error => {
        this.apiStatus = 'API no disponible';
        this.recordError(error);
      }
    });
  }

  private setStat(label: string, value: string): void {
    this.systemStats = this.systemStats.map(stat => stat.label === label ? { ...stat, value } : stat);
  }

  private recordError(error: unknown): void {
    this.errorMessage = getApiErrorMessage(error, 'No se pudieron cargar algunos datos del panel.');
  }
}
