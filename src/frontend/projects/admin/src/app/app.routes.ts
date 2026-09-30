import { Routes } from '@angular/router';

import { adminSessionGuard, signedInGuard } from './core/auth/auth-guards';

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
