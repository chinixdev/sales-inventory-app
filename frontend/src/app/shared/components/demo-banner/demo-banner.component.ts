import { Component, inject } from '@angular/core';
import { DemoBackend } from '../../../core/demo/demo-backend';

/** Identifica el entorno de demostración y permite restablecer los datos de ejemplo. */
@Component({
  selector: 'app-demo-banner',
  standalone: true,
  template: `
    <div class="demo-banner" role="status">
      <span><strong>Modo demostración</strong> · datos ficticios guardados solo en este navegador; sin conexión al backend real.</span>
      <button type="button" (click)="reset()">Restablecer datos</button>
    </div>`,
  styles: [`
    .demo-banner { display: flex; flex-wrap: wrap; align-items: center; justify-content: center; gap: .5rem 1rem; padding: .5rem 1rem; background: #fbf1d0; color: #5c4710; border-bottom: 1px solid #e3cf8c; font-size: .85rem; }
    button { min-height: 32px; padding: .2rem .8rem; cursor: pointer; background: #fff; color: inherit; border: 1px solid #b9993a; border-radius: 6px; }
    button:focus-visible { outline: 2px solid #5c4710; outline-offset: 2px; }
  `]
})
export class DemoBannerComponent {
  private readonly backend = inject(DemoBackend);

  protected reset(): void {
    if (window.confirm('¿Restablecer los datos de demostración a su estado original?')) {
      this.backend.reset();
      window.location.reload();
    }
  }
}
