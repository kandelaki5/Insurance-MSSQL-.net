import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
export type Status = string;
export interface Policy {
  id: string;
  number: string;
  client: string;
  initials: string;
  vehicle: string;
  plate: string;
  product: string;
  premium: number;
  currency: string;
  paid: number;
  outstanding: number | null;
  status: Status;
  from: string;
  to: string;
  reinsured: boolean;
  ri: number;
}
export interface DashboardData {
  totals: { parties: number; classes: number; pending: number; complaints: number };
  portfolio: { active: number; allPolicies: number; activePremiumGel: number; cededGel: number };
  reminders: { count: number; label: string; href: string }[];
  lastRates: { currency: string; rate: number; rateDate: string }[];
  asOf: string;
}
export const statusLabel = (s: Status) =>
  ({
    active: 'მოქმედი',
    pending_approval: 'დასამტკიცებელი',
    cancelled: 'გაუქმებული',
    draft: 'მონახაზი',
    replaced: 'ჩანაცვლებული',
    expired: 'ვადაგასული',
  })[s] ?? s;
@Injectable({ providedIn: 'root' })
export class DemoStore {
  private http = inject(HttpClient);
  readonly policies = signal<Policy[]>([]);
  readonly dashboard = signal<DashboardData | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly canWrite = signal(false);
  readonly query = signal('');
  readonly active = computed(() => this.policies().filter((p) => p.status === 'active'));
  readonly pending = computed(() => this.policies().filter((p) => p.status === 'pending_approval'));
  readonly premium = computed(() => this.dashboard()?.portfolio.activePremiumGel ?? 0);
  readonly ceded = computed(() => this.dashboard()?.portfolio.cededGel ?? 0);
  readonly paid = computed(() =>
    this.policies()
      .filter((p) => p.currency === 'GEL')
      .reduce((n, p) => n + p.paid, 0),
  );
  readonly outstanding = computed(() =>
    this.policies()
      .filter((p) => p.currency === 'GEL')
      .reduce((n, p) => n + (p.outstanding ?? 0), 0),
  );
  constructor() {
    this.http
      .get<DashboardData>('/?handler=Data')
      .subscribe({
        next: (data) => this.dashboard.set(data),
        error: () =>
          this.error.set('მიმოხილვა ვერ ჩაიტვირთა. გადაამოწმე კავშირი და ხელახლა სცადე.'),
      });
    this.load();
  }
  load(q = '') {
    this.loading.set(true);
    this.error.set('');
    this.query.set(q);
    this.http
      .get<{ rows: Omit<Policy, 'initials' | 'ri'>[]; canWrite: boolean }>(
        '/policies?handler=Data&q=' + encodeURIComponent(q),
      )
      .subscribe({
        next: (data) => {
          if (!Array.isArray(data.rows)) {
            this.error.set('სესია დასრულდა. გთხოვ, ხელახლა შეხვიდე.');
            this.loading.set(false);
            return;
          }
          this.policies.set(
            data.rows.map((p) => ({
              ...p,
              initials: p.client
                .split(' ')
                .filter(Boolean)
                .slice(0, 2)
                .map((w) => w[0])
                .join(''),
              ri: p.reinsured ? 1 : 0,
            })),
          );
          this.canWrite.set(data.canWrite);
          this.loading.set(false);
        },
        error: () => {
          this.loading.set(false);
          this.error.set('მონაცემები ვერ ჩაიტვირთა. გადაამოწმე კავშირი ან ხელახლა შედი.');
        },
      });
  }
  detail(id: string) {
    return this.http.get<any>('/policies/' + encodeURIComponent(id) + '?handler=Data');
  }
}
