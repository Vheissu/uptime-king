import { Routes } from '@angular/router';
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  ...['overview', 'monitors', 'incidents', 'clients', 'notifications'].map((path) => ({
    path,
    loadComponent: () => import('./workspace').then((m) => m.Workspace),
    data: { view: path },
  })),
  { path: '**', redirectTo: 'overview' },
];
