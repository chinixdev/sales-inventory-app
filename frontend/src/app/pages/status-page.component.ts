import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IconComponent, IconName } from '../shared/components/icon/icon.component';

/** Página de estado reutilizable (403, 404). */
@Component({
  selector: 'app-status-page',
  standalone: true,
  imports: [RouterLink, IconComponent],
  template: `
    <section class="status" aria-labelledby="status-title">
      <span class="status-icon"><app-icon [name]="icon()" /></span>
      <p class="code">{{ code() }}</p>
      <h1 id="status-title">{{ title() }}</h1>
      <p class="text">{{ description() }}</p>
      <a routerLink="/dashboard" class="back">Volver al dashboard</a>
    </section>`,
  styles: [`
    .status { max-width: 460px; margin: 4rem auto; padding: 2.5rem 1.5rem; text-align: center; }
    .status-icon { display: inline-grid; place-items: center; width: 52px; height: 52px; border-radius: 50%; border: 1.5px solid var(--color-gold); color: var(--color-primary); }
    .code { margin: 1rem 0 0; font-size: .85rem; letter-spacing: .15em; color: #7a6538; }
    h1 { margin: .25rem 0 .5rem; font-size: 1.6rem; color: var(--color-primary); }
    .text { margin: 0 0 1.5rem; color: var(--color-text-muted); }
    .back { display: inline-block; padding: .65rem 1.2rem; border-radius: 8px; background: var(--color-primary); color: #fff; text-decoration: none; }
    .back:focus-visible { outline: 2px solid var(--color-gold); outline-offset: 2px; }
  `]
})
export class StatusPageComponent {
  readonly icon = input.required<IconName>();
  readonly code = input.required<string>();
  readonly title = input.required<string>();
  readonly description = input.required<string>();
}
