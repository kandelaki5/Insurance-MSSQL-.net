import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, expand, map, reduce, EMPTY } from 'rxjs';
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
/** What the policies list asks the server for (GET /api/v1/policies): the whole database is searched, filtered and paged there. */
export interface PolicyQuery {
  search: string;
  status: string;
  payment: string;
  oldest: boolean;
  offset: number;
  limit: number;
}
interface PolicyListResponse {
  items: PolicyListItem[];
  total: number;
  counts: Record<string, number>;
  asOf: string;
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
  /** The page of policies the list shows. */
  readonly policies = signal<Policy[]>([]);
  /** How many policies the list's search and filters find, all pages together. */
  readonly total = signal(0);
  /** How many policies the search finds by status (the tabs), before the status and payment filters. */
  readonly counts = signal<Record<string, number>>({});
  /** The home page's newest policies and the ones waiting for approval. */
  readonly latest = signal<Policy[]>([]);
  readonly pendingPreview = signal<Policy[]>([]);
  readonly overviewCounts = signal<Record<string, number>>({});
  readonly dashboard = signal<DashboardData | null>(null);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly canWrite = signal(false);
  readonly allCount = computed(() => Object.values(this.counts()).reduce((n, c) => n + c, 0));
  readonly activeCount = computed(() => this.counts()['active'] ?? 0);
  readonly pendingCount = computed(() => this.counts()['pending_approval'] ?? 0);
  readonly overviewPending = computed(() => this.overviewCounts()['pending_approval'] ?? 0);
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
  private request = 0;
  constructor() {
    this.http.get<DashboardData>('/?handler=Data').subscribe({
      next: (data) => this.dashboard.set(data),
      error: () => this.error.set('მიმოხილვა ვერ ჩაიტვირთა. გადაამოწმე კავშირი და ხელახლა სცადე.'),
    });
    this.http.get<{ canWrite: boolean }>('/api/v1/me').subscribe({
      next: (me) => this.canWrite.set(me.canWrite),
      error: () => this.canWrite.set(false),
    });
  }
  private params(q: Partial<PolicyQuery>) {
    let p = new HttpParams()
      .set('search', q.search ?? '')
      .set('offset', q.offset ?? 0)
      .set('limit', q.limit ?? 50);
    if (q.status && q.status !== 'all') p = p.set('status', q.status);
    if (q.payment && q.payment !== 'all') p = p.set('payment', q.payment);
    if (q.oldest) p = p.set('sort', 'oldest');
    return p;
  }
  private fetch(q: Partial<PolicyQuery>) {
    return this.http.get<PolicyListResponse>('/api/v1/policies', { params: this.params(q) });
  }
  private failed(e: HttpErrorResponse) {
    this.loading.set(false);
    this.error.set(
      e.status === 401
        ? 'სესია დასრულდა. გთხოვ, ხელახლა შეხვიდე.'
        : 'მონაცემები ვერ ჩაიტვირთა. გადაამოწმე კავშირი ან ხელახლა შედი.',
    );
  }
  /** One page of the list; an answer that arrives after a newer request was sent is dropped. */
  load(q: PolicyQuery) {
    const mine = ++this.request;
    this.loading.set(true);
    this.error.set('');
    this.fetch(q).subscribe({
      next: (data) => {
        if (mine !== this.request) return;
        this.policies.set(data.items.map(toPolicy));
        this.total.set(data.total);
        this.counts.set(data.counts);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => mine === this.request && this.failed(e),
    });
  }
  /** The home page: the four newest policies and the first two waiting for approval, with the counts. */
  loadOverview() {
    this.loading.set(true);
    this.error.set('');
    this.fetch({ limit: 4 }).subscribe({
      next: (data) => {
        this.latest.set(data.items.map(toPolicy));
        this.overviewCounts.set(data.counts);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => this.failed(e),
    });
    this.fetch({ status: 'pending_approval', limit: 2 }).subscribe({
      next: (data) => this.pendingPreview.set(data.items.map(toPolicy)),
      error: () => this.pendingPreview.set([]),
    });
  }
  /** Every policy the search and the filters find, page after page of 200 (for the file the list exports). */
  all(q: PolicyQuery): Observable<Policy[]> {
    const page = (offset: number) =>
      this.fetch({ ...q, offset, limit: 200 }).pipe(map((data) => ({ data, offset })));
    return page(0).pipe(
      expand(({ data, offset }) =>
        offset + data.items.length < data.total && data.items.length ? page(offset + 200) : EMPTY,
      ),
      reduce((rows: Policy[], { data }) => rows.concat(data.items.map(toPolicy)), []),
    );
  }
  detail(id: string) {
    return this.http.get<PolicyCard>('/api/v1/policies/' + encodeURIComponent(id));
  }
}

function toPolicy(p: PolicyListItem): Policy {
  return {
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
  };
}
