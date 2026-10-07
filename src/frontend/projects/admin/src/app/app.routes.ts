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
    // PAY-01 / ADM-09 (W3-03): doc 11 P7 step 4's queue. Same guard rule as every other admin route — the
    // API is the security boundary (doc 03 §3a), and a user without Orders.View is shown the refusal
    // rather than bounced from a page that then cannot explain itself.
    path: 'orders/bank-transfers',
    title: `Bank transfers — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/bank-transfers/bank-transfers-page').then((m) => m.BankTransfersPage),
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
    // ADM-10 (W1-03). Like every other admin route the guard only asks for a session: the API is the
    // security boundary (doc 03 §3a), and a user without Products.View is shown the refusal rather than
    // bounced from a page that then cannot explain itself.
    path: 'products',
    title: `Products — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/products/products-list-page').then((m) => m.ProductsListPage),
  },
  {
    path: 'products/new',
    title: `Add a product — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/products/product-form-page').then((m) => m.ProductFormPage),
  },
  {
    path: 'products/:id',
    title: `Edit product — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/products/product-form-page').then((m) => m.ProductFormPage),
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
    // ADM-17 / GLB-10 (W2-11). Like every other admin route the guard only asks for a session: the API
    // is the security boundary (doc 03 §3a), and a user without Content.View is shown the refusal rather
    // than bounced from a page that then cannot explain itself.
    path: 'content/pages',
    title: `Content pages — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/content/content-pages-page').then((m) => m.ContentPagesPage),
  },
  {
    path: 'content/pages/new',
    title: `Add a content page — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/content/content-page-form-page').then((m) => m.ContentPageFormPage),
  },
  {
    path: 'content/pages/:id',
    title: `Edit content page — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/content/content-page-form-page').then((m) => m.ContentPageFormPage),
  },
  {
    // GLB-10 (W2-11). Behind Customers.* on the API, not Content.*: doc 08 §3.7's G-06 resolution puts
    // a contact enquiry in the Customers module, because it is customer data that happens to arrive
    // through a content page.
    path: 'enquiries',
    title: `Contact enquiries — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/enquiries/enquiries-page').then((m) => m.EnquiriesPage),
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
