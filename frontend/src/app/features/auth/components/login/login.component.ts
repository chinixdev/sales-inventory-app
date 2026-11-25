import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../../../core/services/auth.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';
import { DEMO_USER } from '../../../../core/demo/demo-data';
import { isDemoMode } from '../../../../core/demo/demo-mode';
import { DemoBannerComponent } from '../../../../shared/components/demo-banner/demo-banner.component';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule, DemoBannerComponent],
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.scss']
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  email = '';
  password = '';
  protected readonly isDemo = isDemoMode;
  protected readonly isLoading = signal(false);
  protected readonly errorMessage = signal('');

  /** Modo demo: entra con el usuario ficticio sin escribir credenciales. */
  loginAsDemo(): void {
    this.email = DEMO_USER.email;
    this.password = DEMO_USER.password;
    this.onLogin();
  }

  onLogin(): void {
    if (this.isLoading()) {
      return;
    }

    this.errorMessage.set('');
    this.isLoading.set(true);
    this.auth.login({ email: this.email.trim(), password: this.password }).subscribe({
      next: () => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        const destination = returnUrl?.startsWith('/') && !returnUrl.startsWith('//')
          ? returnUrl
          : '/dashboard';
        void this.router.navigateByUrl(destination);
      },
      error: error => {
        this.errorMessage.set(getApiErrorMessage(error, 'No se pudo iniciar sesión. Revisa la conexión con la API.'));
        this.isLoading.set(false);
      }
    });
  }
}
