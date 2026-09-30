import { Routes } from '@angular/router';

import { adminSessionGuard, signedInGuard, twoFactorSetupGuard } from './core/auth/auth-guards';

export const routes: Routes = [
  {
    path: '',
    title: 'Spencer Denim Admin',
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/dashboard/dashboard-page').then((m) => m.DashboardPage),
  },
  {
    path: 'login',
    title: 'Sign in — Spencer Denim Admin',
    loadComponent: () => import('./pages/login/login-page').then((m) => m.LoginPage),
  },
  {
    // No guard: only the API knows whether this browser holds a pending sign-in, and says so on submit.
    path: 'login/two-factor',
    title: 'Two-factor authentication — Spencer Denim Admin',
    loadComponent: () =>
      import('./pages/two-factor-login/two-factor-login-page').then((m) => m.TwoFactorLoginPage),
  },
  {
    path: 'change-password',
    title: 'Change password — Spencer Denim Admin',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./pages/change-password/change-password-page').then((m) => m.ChangePasswordPage),
  },
  {
    path: 'audit-log',
    title: 'Audit log — Spencer Denim Admin',
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/audit-log/audit-log-page').then((m) => m.AuditLogPage),
  },
  { path: '**', redirectTo: '' },
];
