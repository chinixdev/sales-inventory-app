import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export type ViewState = 'loading' | 'error' | 'empty';

/** Estado reutilizable de carga, error o vacío. Usa roles ARIA adecuados a cada estado. */
@Component({
  selector: 'app-state-message',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (state()) {
      @case ('loading') {
        <div class="state" role="status" aria-live="polite">
          <span class="spinner" aria-hidden="true"></span>
          <p>{{ message() || 'Cargando…' }}</p>
        </div>
      }
      @case ('error') {
        <div class="state state-error" role="alert">
          <p>{{ message() || 'Ocurrió un error inesperado.' }}</p>
          @if (retryable()) {
            <button type="button" class="retry" (click)="retry.emit()">Reintentar</button>
          }
        </div>
      }
      @default {
        <div class="state" role="status">
          <p>{{ message() || 'No hay información para mostrar.' }}</p>
          @if (hint()) { <small>{{ hint() }}</small> }
        </div>
      }
    }`,
  styles: [`
    .state { display: grid; justify-items: center; gap: .5rem; padding: 2rem 1rem; text-align: center; color: var(--color-text-muted); }
    .state p { margin: 0; }
    .state-error { color: #8a1c1c; background: #fdf0ef; border: 1px solid #f1c9c6; border-radius: var(--radius); }
    .spinner { width: 28px; height: 28px; border: 3px solid var(--color-border); border-top-color: var(--color-primary); border-radius: 50%; animation: spin .8s linear infinite; }
    .retry { padding: .45rem .9rem; border: 1px solid currentColor; border-radius: 6px; background: #fff; color: inherit; cursor: pointer; }
    @keyframes spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .spinner { animation-duration: 3s; } }
  `]
})
export class StateMessageComponent {
  readonly state = input<ViewState>('loading');
  readonly message = input<string>('');
  readonly hint = input<string>('');
  readonly retryable = input<boolean>(false);
  readonly retry = output<void>();
}
