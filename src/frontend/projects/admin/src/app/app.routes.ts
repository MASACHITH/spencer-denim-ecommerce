import { Routes } from '@angular/router';

import { adminSessionGuard, signedInGuard, twoFactorSetupGuard } from './core/auth/auth-guards';
import { BRAND } from '@spencer-denim/shared';

export const routes: Routes = [
  {
    path: '',
    title: BRAND.adminName,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/dashboard/dashboard-page').then((m) => m.DashboardPage),
  },
  {
    path: 'login',
    title: `Sign in — ${BRAND.adminName}`,
    loadComponent: () => import('./pages/login/login-page').then((m) => m.LoginPage),
  },
  {
    // No guard: only the API knows whether this browser holds a pending sign-in, and says so on submit.
    path: 'login/two-factor',
    title: `Two-factor authentication — ${BRAND.adminName}`,
    loadComponent: () =>
      import('./pages/two-factor-login/two-factor-login-page').then((m) => m.TwoFactorLoginPage),
  },
  {
    path: 'change-password',
    title: `Change password — ${BRAND.adminName}`,
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./pages/change-password/change-password-page').then((m) => m.ChangePasswordPage),
  },
  {
    path: 'audit-log',
    title: `Audit log — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/audit-log/audit-log-page').then((m) => m.AuditLogPage),
  },
  {
    path: 'two-factor/setup',
    title: `Set up two-factor authentication — ${BRAND.adminName}`,
    canActivate: [twoFactorSetupGuard],
    loadComponent: () =>
      import('./pages/two-factor-setup/two-factor-setup-page').then((m) => m.TwoFactorSetupPage),
  },
  {
    path: 'security',
    title: `Account security — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/security/security-page').then((m) => m.SecurityPage),
  },
  { path: '**', redirectTo: '' },
];
