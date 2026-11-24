import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfirmDialogComponent } from './confirm-dialog.component';

@Component({
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    <button id="opener" (click)="open.set(true)">Abrir</button>
    <app-confirm-dialog [open]="open()" title="Eliminar" message="¿Seguro?" (confirmed)="confirmed = true" (cancelled)="open.set(false)" />`
})
class HostComponent {
  open = signal(false);
  confirmed = false;
}

describe('ConfirmDialogComponent', () => {
  it('focuses Cancel on open, closes with Escape and restores focus to the opener', async () => {
    const fixture = TestBed.createComponent(HostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    const opener = fixture.nativeElement.querySelector('#opener') as HTMLButtonElement;
    opener.focus();

    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('[role="alertdialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(document.activeElement?.textContent).toContain('Cancelar');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    await Promise.resolve();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alertdialog"]')).toBeNull();
    expect(document.activeElement).toBe(opener);
    fixture.nativeElement.remove();
  });

  it('keeps Tab focus inside the dialog', () => {
    const fixture = TestBed.createComponent(HostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.componentInstance.open.set(true);
    fixture.detectChanges();
    const buttons = fixture.nativeElement.querySelectorAll('[role="alertdialog"] button') as NodeListOf<HTMLButtonElement>;
    buttons[1].focus();
    const event = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    buttons[1].dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(buttons[0]);
    fixture.nativeElement.remove();
  });
});
