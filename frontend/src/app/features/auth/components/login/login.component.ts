import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../../../core/services/auth.service';
import { getApiErrorMessage } from '../../../../core/models/api-response.model';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.scss']
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  email = '';
  password = '';
  isLoading = false;
  errorMessage = '';

  onLogin(): void {
    if (this.isLoading) {
      return;
    }

    this.errorMessage = '';
    this.isLoading = true;
    this.auth.login({ email: this.email.trim(), password: this.password }).subscribe({
      next: () => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        const destination = returnUrl?.startsWith('/') && !returnUrl.startsWith('//')
          ? returnUrl
          : '/dashboard';
        void this.router.navigateByUrl(destination);
      },
      error: error => {
        this.errorMessage = getApiErrorMessage(error, 'No se pudo iniciar sesión. Revisa la conexión con la API.');
        this.isLoading = false;
      }
    });
  }
}
