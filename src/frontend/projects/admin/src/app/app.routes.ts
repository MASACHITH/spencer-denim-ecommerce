import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Spencer Denim Admin',
    loadComponent: () => import('./pages/dashboard/dashboard-page').then((m) => m.DashboardPage),
  },
  { path: '**', redirectTo: '' },
];
