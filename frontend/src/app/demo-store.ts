import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Money, sumMoney } from './money';
export type Status = string;
export interface Policy {
  id: string;
  number: string;
  client: string;
  initials: string;
  vehicle: string;
  plate: string;
  product: string;
  premium: Money;
  currency: string;
  paid: Money;
  outstanding: Money | null;
  status: Status;
  from: string;
  to: string;
  reinsured: boolean;
  ri: number;
}
/** One row of GET /api/v1/policies. */
export interface PolicyListItem {
  id: string;
  number: string;
  policyholder: string;
  product: string | null;
  objects: string | null;
  effectiveFrom: string;
  effectiveTo: string;
  status: Status;
  premium: Money;
  paid: Money;
  outstanding: Money | null;
  reinsured: boolean;
}
/** GET /api/v1/policies/{id}: the card's figures every reader sees. */
export interface PolicyCard {
  id: string;
  number: string;
  policyholder: string;
  product: string | null;
  objects: string;
  effectiveFrom: string;
  effectiveTo: string;
  status: Status;
  currency: string;
  premium: Money;
  issuedPremium: Money;
  receivable: {
    outstanding: Money;
    outstandingGel: Money;
    overdue: Money;
    overpaid: Money;
    valuationRate: string;
    valuationDate: string;
  };
  schedule: { dueOn: string; amount: Money; outstanding: Money }[];
  cessions: {
    treatyId: string;
    treatyNumber: string;
    treatyName: string;
    sharePercent: string;
    amountGel: Money;
    bookedOn: string;
    isCancellation: boolean;
  }[];
  asOf: string;
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
    sumMoney(
      this.policies()
        .filter((p) => p.currency === 'GEL')
        .map((p) => p.paid),
      'GEL',
    ),
  );
  readonly outstanding = computed(() =>
    sumMoney(
      this.policies()
        .filter((p) => p.currency === 'GEL' && p.outstanding)
        .map((p) => p.outstanding!),
      'GEL',
    ),
  );
  constructor() {
    this.http.get<DashboardData>('/?handler=Data').subscribe({
      next: (data) => this.dashboard.set(data),
      error: () => this.error.set('მიმოხილვა ვერ ჩაიტვირთა. გადაამოწმე კავშირი და ხელახლა სცადე.'),
    });
    this.load();
  }
  load(q = '') {
    this.loading.set(true);
    this.error.set('');
    this.query.set(q);
    this.http.get<{ canWrite: boolean }>('/api/v1/me').subscribe({
      next: (me) => this.canWrite.set(me.canWrite),
      error: () => this.canWrite.set(false),
    });
    this.http
      .get<{ items: PolicyListItem[]; total: number }>(
        '/api/v1/policies?limit=200&search=' + encodeURIComponent(q),
      )
      .subscribe({
        next: (data) => {
          this.policies.set(
            data.items.map((p) => ({
              id: p.id,
              number: p.number,
              client: p.policyholder,
              initials: p.policyholder
                .split(' ')
                .filter(Boolean)
                .slice(0, 2)
                .map((w) => w[0])
                .join(''),
              vehicle: p.objects ?? '—',
              plate: '',
              product: p.product ?? '',
              premium: p.premium,
              currency: p.premium.currency,
              paid: p.paid,
              outstanding: p.outstanding,
              status: p.status,
              from: p.effectiveFrom,
              to: p.effectiveTo,
              reinsured: p.reinsured,
              ri: p.reinsured ? 1 : 0,
            })),
          );
          this.loading.set(false);
        },
        error: (e: HttpErrorResponse) => {
          this.loading.set(false);
          this.error.set(
            e.status === 401
              ? 'სესია დასრულდა. გთხოვ, ხელახლა შეხვიდე.'
              : 'მონაცემები ვერ ჩაიტვირთა. გადაამოწმე კავშირი ან ხელახლა შედი.',
          );
        },
      });
  }
  detail(id: string) {
    return this.http.get<PolicyCard>('/api/v1/policies/' + encodeURIComponent(id));
  }
}
