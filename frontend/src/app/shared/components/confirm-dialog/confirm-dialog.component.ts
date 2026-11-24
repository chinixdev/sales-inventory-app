import { AfterViewChecked, Component, ElementRef, HostListener, effect, input, output, viewChild } from '@angular/core';

/** Diálogo de confirmación accesible: foco inicial en «Cancelar», foco atrapado, Esc y restauración del foco. */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  template: `
    @if (open()) {
      <div class="overlay" (click)="cancelled.emit()"></div>
      <div #dialog class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="confirm-title"
           aria-describedby="confirm-msg" (keydown)="trapFocus($event)">
        <h2 id="confirm-title">{{ title() }}</h2>
        <p id="confirm-msg">{{ message() }}</p>
        <div class="actions">
          <button #cancelBtn type="button" class="btn-cancel" (click)="cancelled.emit()">Cancelar</button>
          <button type="button" class="btn-confirm" (click)="confirmed.emit()">{{ confirmLabel() }}</button>
        </div>
      </div>
    }`,
  styles: [`
    .overlay { position: fixed; inset: 0; z-index: 90; background: rgba(21, 44, 40, .55); }
    .dialog { position: fixed; z-index: 91; top: 50%; left: 50%; transform: translate(-50%, -50%); width: min(440px, calc(100vw - 2rem)); max-height: calc(100dvh - 2rem); overflow-y: auto; padding: 1.5rem; background: #fff; border-radius: 12px; border-top: 3px solid var(--color-gold); box-shadow: 0 16px 40px rgba(0,0,0,.25); }
    h2 { margin: 0 0 .5rem; font-size: 1.15rem; color: var(--color-primary); }
    p { margin: 0 0 1.25rem; color: var(--color-text-muted); overflow-wrap: anywhere; }
    .actions { display: flex; justify-content: flex-end; gap: .6rem; flex-wrap: wrap; }
    button { min-height: 40px; padding: .5rem 1.1rem; border-radius: 6px; cursor: pointer; border: 1px solid var(--color-border-strong); background: #fff; color: var(--color-text); }
    button:hover { border-color: var(--color-gold); }
    button:focus-visible { outline: 2px solid var(--color-gold); outline-offset: 2px; }
    .btn-confirm { background: #a32626; border-color: #a32626; color: #fff; }
    .btn-confirm:hover { background: #8a1c1c; border-color: #8a1c1c; }
    @media (max-width: 480px) { .actions { flex-direction: column-reverse; } .actions button { width: 100%; } }
  `]
})
export class ConfirmDialogComponent implements AfterViewChecked {
  readonly open = input<boolean>(false);
  readonly title = input<string>('Confirmar acción');
  readonly message = input<string>('');
  readonly confirmLabel = input<string>('Eliminar');
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  private readonly cancelBtn = viewChild<ElementRef<HTMLButtonElement>>('cancelBtn');
  private readonly dialog = viewChild<ElementRef<HTMLElement>>('dialog');
  private focused = false;
  private previouslyFocused: HTMLElement | null = null;

  constructor() {
    // Al abrir se recuerda el elemento activo; al cerrar se devuelve el foco allí.
    effect(() => {
      if (this.open()) {
        this.previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      } else if (this.previouslyFocused) {
        const target = this.previouslyFocused;
        this.previouslyFocused = null;
        queueMicrotask(() => target.isConnected && target.focus());
      }
    });
  }

  ngAfterViewChecked(): void {
    const button = this.cancelBtn();
    if (button && !this.focused) {
      button.nativeElement.focus();
      this.focused = true;
    } else if (!button) {
      this.focused = false;
    }
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.open()) {
      this.cancelled.emit();
    }
  }

  protected trapFocus(event: KeyboardEvent): void {
    if (event.key !== 'Tab') {
      return;
    }

    const buttons = Array.from(this.dialog()?.nativeElement.querySelectorAll<HTMLElement>('button') ?? []);
    const first = buttons[0];
    const last = buttons[buttons.length - 1];
    if (!first || !last) {
      return;
    }

    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
}
