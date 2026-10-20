import { Routes } from '@angular/router';

import { adminSessionGuard, signedInGuard, twoFactorSetupGuard } from './core/auth/auth-guards';
import { BRAND } from '@spencer-denim/shared';

export const routes: Routes = [
  {
    // ADM-04, ADM-05, ADM-07, ADM-08 (W4-02): the dashboard.
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
    // ADM-09 (W3-04): the order list, which is doc 11 P9 step 1 and doc 11 §6.6's "a customer phones about
    // an order". Like every other admin route the guard only asks for a session: the API is the security
    // boundary (doc 03 §3a), and a user without Orders.View is shown the refusal rather than bounced from
    // a page that then cannot explain itself.
    path: 'orders',
    title: `Orders — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/orders/orders-list-page').then((m) => m.OrdersListPage),
  },
  {
    // PAY-01 / ADM-09 (W3-03): doc 11 P7 step 4's queue. **Declared before `orders/:orderPublicId`**,
    // because Angular matches routes in order and `bank-transfers` would otherwise be read as an order
    // token — which the `:orderPublicId` route would then fetch and 404 on.
    path: 'orders/bank-transfers',
    title: `Bank transfers — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/bank-transfers/bank-transfers-page').then((m) => m.BankTransfersPage),
  },
  {
    // ADM-13 (W3-05): the promo codes the client issues. Like every other admin route the guard only
    // asks for a session: the API is the security boundary (doc 03 §3a), and a user without
    // Promotions.View is shown the refusal rather than bounced from a page that then cannot explain
    // itself.
    //
    // Sale campaigns (ADM-14) sit beside this under the same `marketing/` prefix, which is what this
    // route being named for the codes it serves rather than for the module was for.
    path: 'marketing/promo-codes',
    title: `Promo codes — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/marketing/promo-codes-page').then((m) => m.PromoCodesPage),
  },
  {
    // ADM-14 (W3-05c): the sale campaigns the client runs. The guard asks only for a session, as every
    // other admin route does: the API is the security boundary (doc 03 §3a), and a user without
    // Promotions.View is shown the refusal rather than bounced from a page that then cannot explain
    // itself.
    path: 'marketing/sale-campaigns',
    title: `Sale campaigns — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/marketing/sale-campaigns-page').then((m) => m.SaleCampaignsPage),
  },
  {
    // ADM-09 / ADM-22 (W3-04): the couriers the client fills in. Also before `orders/:orderPublicId`.
    path: 'orders/couriers',
    title: `Couriers — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/orders/couriers-page').then((m) => m.CouriersPage),
  },
  {
    // ADM-09 (W3-04). The order detail, and issue #124's route to an order that has already expired: the
    // bank queue cannot reach one, and this page is reached by searching the number the customer quotes.
    path: 'orders/:orderPublicId',
    title: `Order — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/orders/order-detail-page').then((m) => m.OrderDetailPage),
  },
  {
    // ADM-09 (W3-04), doc 11 P9 step 3's invoice and packing copy.
    path: 'orders/:orderPublicId/invoice',
    title: `Invoice — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/orders/order-invoice-page').then((m) => m.OrderInvoicePage),
  },
  {
    // ADM-15 (W3-07): registered customers. Like every other admin route the guard only asks for a session:
    // the API is the security boundary (doc 03 §3a), and a user without Customers.View is shown the refusal
    // rather than bounced from a page that then cannot explain itself.
    path: 'customers',
    title: `Customers — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/customers/customers-list-page').then((m) => m.CustomersListPage),
  },
  {
    path: 'customers/:customerId',
    title: `Customer — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/customers/customer-detail-page').then((m) => m.CustomerDetailPage),
  },
  {
    // ADM-21 (W3-07): newsletter subscribers and their audited export (Newsletter.View / Newsletter.Export).
    path: 'marketing/newsletter',
    title: `Newsletter subscribers — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () =>
      import('./pages/newsletter/newsletter-subscribers-page').then((m) => m.NewsletterSubscribersPage),
  },
  {
    // ADM-12 (W1-05, #190): stock per variant, the bulk update, the low-stock threshold and the movement
    // history. The guard asks only for a session, as every other admin route does: the API is the security
    // boundary (Inventory.View to look, Inventory.Edit to change), and the page says so to a role without
    // Inventory.View rather than bouncing it.
    path: 'inventory',
    title: `Inventory — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/inventory/inventory-page').then((m) => m.InventoryPage),
  },
  {
    // ADM-16 (#189): review moderation. Session guard only, like every admin route; the API enforces
    // Reviews.View / Reviews.Approve / Reviews.Edit and the page says so to a role without them.
    path: 'reviews',
    title: `Reviews — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/reviews/reviews-page').then((m) => m.ReviewsPage),
  },
  {
    // ADM-19 (W4-02): the Sales, Inventory and Customers reports and their audited exports. The guard asks
    // only for a session, as every other admin route does: the API is the security boundary (doc 03 §3a),
    // and the page itself says so to a role without Reports.View rather than bouncing it.
    path: 'reports',
    title: `Reports — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/reports/reports-page').then((m) => m.ReportsPage),
  },
  {
    // ADM-20 (W4-03): visits, sources, devices and top pages. The guard asks only for a session, as every other
    // admin route does: the API is the security boundary, and the page says so to a role without Traffic.View.
    path: 'traffic',
    title: `Traffic — ${BRAND.adminName}`,
    canActivate: [adminSessionGuard],
    loadComponent: () => import('./pages/traffic/traffic-page').then((m) => m.TrafficPage),
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
