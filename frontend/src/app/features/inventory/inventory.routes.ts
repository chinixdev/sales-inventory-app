import { Routes } from '@angular/router';
import { StockMovementsComponent } from './components/stock-movements/stock-movements.component';

export const INVENTORY_ROUTES: Routes = [
  {
    path: '',
    component: StockMovementsComponent
  }
];
