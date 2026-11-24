import { Component } from '@angular/core';
import { StatusPageComponent } from './status-page.component';

@Component({
  selector: 'app-access-denied',
  standalone: true,
  imports: [StatusPageComponent],
  template: `<app-status-page icon="lock" code="403" title="Acceso denegado"
    description="Tu usuario no tiene permisos para ver esta sección. Si crees que es un error, contacta a un administrador." />`
})
export class AccessDeniedComponent {}
