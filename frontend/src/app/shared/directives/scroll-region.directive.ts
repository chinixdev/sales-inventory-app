import { Directive, ElementRef, Injector, afterNextRender, inject, signal } from '@angular/core';

/**
 * Hace accesible por teclado cualquier contenedor `.table-wrap` con desplazamiento horizontal:
 * lo expone como región enfocable y la nombra con el `<caption>` de su tabla.
 */
@Directive({
  selector: '.table-wrap',
  standalone: true,
  host: {
    tabindex: '0',
    role: 'region',
    '[attr.aria-label]': 'label()'
  }
})
export class ScrollRegionDirective {
  protected readonly label = signal('Tabla con desplazamiento horizontal');
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    afterNextRender(() => {
      const caption = this.element.nativeElement.querySelector('caption')?.textContent?.trim();
      if (caption) {
        this.label.set(`${caption} (desplazable horizontalmente)`);
      }
    }, { injector: inject(Injector) });
  }
}
