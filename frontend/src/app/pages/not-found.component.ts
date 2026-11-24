import { Component } from '@angular/core';
import { StatusPageComponent } from './status-page.component';

@Component({
  selector: 'app-not-found',
  standalone: true,
  imports: [StatusPageComponent],
  template: `<app-status-page icon="search" code="404" title="Página no encontrada"
    description="La dirección solicitada no existe o fue movida." />`
})
export class NotFoundComponent {}
