import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Spencer Denim',
    loadComponent: () => import('./pages/home/home-page').then((m) => m.HomePage),
  },
  { path: '**', redirectTo: '' },
];
