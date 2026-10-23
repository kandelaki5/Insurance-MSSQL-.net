import { Routes } from '@angular/router';
import { Dashboard } from './dashboard';
import { Policies } from './policies';
import { NativePage } from './native-page';
export const routes: Routes = [
  { path: '', component: Dashboard },
  { path: 'policies', component: Policies },
  { path: '**', component: NativePage },
];
