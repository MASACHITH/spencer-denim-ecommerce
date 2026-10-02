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
    // ADM-11 (W1-12). Like every other admin route the guard only asks for a session: the API is the
    // security boundary (doc 03 §3a), and a user without Categories.View is shown the refusal rather than
    // bounced from a page that then cannot explain itself.
    path: 'catalog',
    title: `Departments and categories — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/catalog/catalog-page').then((m) => m.CatalogPage),
  },
  {
    // ADM-22 + GLB-02 (W1-16). Like every other admin route the guard only asks for a session: the API is
    // the security boundary (doc 03 §3a), and a user without Settings.Edit is shown the refusal rather than
    // bounced from a page that then cannot explain itself.
    path: 'settings',
    title: `Settings — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/settings/settings-page').then((m) => m.SettingsPage),
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
  {
    path: 'users',
    title: `Staff users — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/users/users-list-page').then((m) => m.UsersListPage),
  },
  {
    path: 'users/new',
    title: `New staff user — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/users/user-form-page').then((m) => m.UserFormPage),
  },
  {
    path: 'users/:id',
    title: `Edit staff user — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/users/user-form-page').then((m) => m.UserFormPage),
  },
  {
    path: 'roles',
    title: `Roles — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/roles/roles-list-page').then((m) => m.RolesListPage),
  },
  { path: '**', redirectTo: '' },
];
