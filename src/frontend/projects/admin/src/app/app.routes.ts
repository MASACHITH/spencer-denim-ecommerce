import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Spencer Denim Admin',
    loadComponent: () => import('./pages/dashboard/dashboard-page').then((m) => m.DashboardPage),
  },
  {
    path: 'login',
    title: 'Sign in — Spencer Denim Admin',
    loadComponent: () => import('./pages/login/login-page').then((m) => m.LoginPage),
  },
  { path: '**', redirectTo: '' },
];
