import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { DemoStore, Policy, PolicyQuery, statusLabel } from './demo-store';
import { Icon } from './icon';
import { MoneyPipe, formatMoney, isZero } from './money';
import { PolicyDetail } from './policy-detail';

@Component({
  selector: 'app-policies',
  imports: [DatePipe, DecimalPipe, FormsModule, Icon, MoneyPipe, PolicyDetail],
  template: `
    @if (store.error()) {
      <div class="workspace-error" role="alert">
        {{ store.error() }} <a href="/login">შესვლა</a>
      </div>
    }
    @if (store.loading()) {
      <p class="page-hint" role="status">მონაცემები იტვირთება…</p>
    }
    <div class="page-heading">
      <div>
        <p class="eyebrow">სადაზღვევო პორტფელი</p>
        <h1>
          პოლისები <span class="heading-count">{{ store.allCount() }}</span>
        </h1>
        <p>იპოვე საჭირო პოლისი და მართე ყველა დეტალი ერთ ადგილას.</p>
      </div>
      <div class="heading-actions">
        <button class="button secondary" (click)="exportCsv()">
          <ui-icon name="download" />ექსპორტი</button
        ><button class="button primary" (click)="openNew()" [disabled]="!store.canWrite()">
          <ui-icon name="plus" />ახალი პოლისი
        </button>
      </div>
    </div>
    <section class="policy-summary" aria-label="პოლისების შეჯამება">
      <div>
        <span class="icon-tile lavender"><ui-icon name="shield" /></span>
        <div>
          <span>მოქმედი პოლისები</span><strong>{{ store.activeCount() }}</strong>
        </div>
      </div>
      <div>
        <span class="icon-tile peach"><ui-icon name="clock" /></span>
        <div>
          <span>დასამტკიცებელი</span><strong>{{ store.pendingCount() }}</strong>
        </div>
      </div>
      <div>
        <span class="icon-tile mint"><ui-icon name="wallet" /></span>
        <div>
          <span>მოქმედი პრემია</span
          ><strong>{{ store.premium() | number: '1.0-0' }} <small>₾</small></strong>
        </div>
      </div>
    </section>
    <section class="panel policies-panel">
      <div class="policy-tabs" role="group" aria-label="სტატუსის ფილტრი">
        @for (tab of tabs; track tab.key) {
          <button
            [class.selected]="status() === tab.key"
            [attr.aria-pressed]="status() === tab.key"
            (click)="setStatus(tab.key)"
          >
            {{ tab.title }}<span>{{ count(tab.key) }}</span>
          </button>
        }
      </div>
      <div class="filter-toolbar">
        <label class="search-field"
          ><ui-icon name="search" /><input
            type="search"
            aria-label="პოლისის ძიება"
            placeholder="პოლისის ნომერი, კლიენტი ან ავტომობილი..."
            [ngModel]="search()"
            (ngModelChange)="onSearch($event)"
        /></label>
        <div class="filter-right">
          <label class="select-field"
            ><ui-icon name="wallet" /><select
              aria-label="გადახდის ფილტრი"
              [ngModel]="payment()"
              (ngModelChange)="payment.set($event); go(1)"
            >
              <option value="all">ყველა გადახდა</option>
              <option value="unpaid">გადაუხდელი ნაშთით</option>
              <option value="paid">სრულად გადახდილი</option>
            </select></label
          ><button
            class="button sort-button"
            (click)="ascending.set(!ascending()); go(1)"
            [attr.aria-label]="ascending() ? 'ჯერ ახალი პოლისები' : 'ჯერ ძველი პოლისები'"
          >
            <ui-icon name="sort" />{{ ascending() ? 'ჯერ ძველი' : 'ჯერ ახალი' }}
          </button>
        </div>
      </div>
      @if (search() || payment() !== 'all' || status() !== 'all') {
        <div class="filter-result">
          <span>მოიძებნა {{ store.total() }} პოლისი</span
          ><button class="text-link" (click)="reset()">
            ფილტრების გასუფთავება<ui-icon name="close" />
          </button>
        </div>
      }
      <div class="table-scroll">
        <table class="policy-table">
          <thead>
            <tr>
              <th>პოლისი / პროდუქტი</th>
              <th>კლიენტი</th>
              <th>ავტომობილი</th>
              <th>მოქმედების ვადა</th>
              <th class="number-cell">პრემია / ნაშთი</th>
              <th>სტატუსი</th>
              <th><span class="sr-only">გახსნა</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of visible(); track p.number) {
              <tr>
                <td>
                  <button class="policy-link" (click)="open(p)">{{ p.number }}</button
                  ><small class="cell-sub"
                    ><ui-icon name="car" />{{ p.product || 'დაზღვევა' }}</small
                  >
                </td>
                <td>
                  <div class="client-cell">
                    <span
                      class="client-avatar"
                      [class.mint-avatar]="$index % 3 === 1"
                      [class.peach-avatar]="$index % 3 === 2"
                      >{{ p.initials }}</span
                    ><span>{{ p.client }}</span>
                  </div>
                </td>
                <td>
                  {{ p.vehicle }}<small class="cell-sub plate">{{ p.plate }}</small>
                </td>
                <td class="date-cell">
                  {{ p.from | date: 'dd.MM.yyyy'
                  }}<small class="cell-sub">{{ p.to | date: 'dd.MM.yyyy' }}</small>
                </td>
                <td class="number-cell">
                  <strong>{{ p.premium | money }} {{ p.currency }}</strong
                  ><small
                    class="cell-sub"
                    [class.green-text]="p.status === 'active' && zero(p.outstanding)"
                  >
                    @if (p.outstanding === null) {
                      დამტკიცების შემდეგ
                    } @else if (zero(p.outstanding)) {
                      სრულად გადახდილი
                    } @else {
                      ნაშთი: {{ p.outstanding | money }} {{ p.currency }}
                    }
                  </small>
                </td>
                <td>
                  <span class="status" [class.pending]="p.outstanding === null"
                    ><i></i>{{ label(p.status) }}</span
                  >
                  @if (p.ri) {
                    <small class="cell-sub ri-indicator">გადაზღვეული</small>
                  }
                </td>
                <td>
                  <button
                    class="icon-button row-open"
                    (click)="open(p)"
                    [attr.aria-label]="p.number + ' გახსნა'"
                  >
                    <ui-icon name="arrow" />
                  </button>
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="7">
                  <div class="empty-state">
                    <span class="icon-tile lavender"><ui-icon name="search" /></span>
                    <h3>პოლისი ვერ მოიძებნა</h3>
                    <p>სცადე სხვა სიტყვა ან გაასუფთავე ფილტრები.</p>
                    <button class="button secondary" (click)="reset()">
                      ფილტრების გასუფთავება
                    </button>
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <div class="pagination">
        <span
          >ნაჩვენებია {{ store.total() ? (page() - 1) * pageSize + 1 : 0 }}–{{ end() }} /
          {{ store.total() }} პოლისი</span
        >
        <div>
          <button
            class="page-button"
            [disabled]="page() === 1"
            aria-label="წინა გვერდი"
            (click)="go(page() - 1)"
          >
            ←
          </button>
          @for (n of pages(); track n) {
            <button
              class="page-button"
              [class.current]="page() === n"
              [attr.aria-current]="page() === n ? 'page' : null"
              [attr.aria-label]="'გვერდი ' + n"
              (click)="go(n)"
            >
              {{ n }}
            </button>
          }
          <button
            class="page-button"
            [disabled]="page() >= pageCount()"
            aria-label="შემდეგი გვერდი"
            (click)="go(page() + 1)"
          >
            →
          </button>
        </div>
      </div>
    </section>
    <p class="page-hint">
      <ui-icon name="info" />ძიება, სტატუსი, გადახდის ფილტრი და დალაგება მთელ ბაზაზე სრულდება.
    </p>
    @if (selected(); as p) {
      <policy-detail [policy]="p" (close)="closeDetail()" />
    }

    @if (toast()) {
      <div class="toast" role="status">
        <span class="mini-check">✓</span>{{ toast()
        }}<button class="icon-button" aria-label="შეტყობინების დახურვა" (click)="toast.set('')">
          <ui-icon name="close" />
        </button>
      </div>
    }
  `,
})
export class Policies {
  readonly store = inject(DemoStore);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  readonly label = statusLabel;
  readonly zero = isZero;
  readonly search = signal('');
  readonly status = signal('all');
  readonly payment = signal('all');
  readonly ascending = signal(false);
  readonly page = signal(1);
  readonly selected = signal<Pick<Policy, 'id' | 'number'> | null>(null);
  readonly toast = signal('');
  readonly pageSize = 7;
  private returnFocus: HTMLElement | null = null;
  readonly tabs = [
    { key: 'all', title: 'ყველა პოლისი' },
    { key: 'active', title: 'მოქმედი' },
    { key: 'pending_approval', title: 'დასამტკიცებელი' },
    { key: 'draft', title: 'მონახაზი' },
    { key: 'cancelled', title: 'გაუქმებული' },
  ];
  /** The page the server gave for the search, the tab, the payment filter and the order (GET /api/v1/policies). */
  readonly visible = computed(() => this.store.policies());
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.store.total() / this.pageSize)));
  /** At most seven page numbers, around the current one. */
  readonly pages = computed(() => {
    const n = this.pageCount();
    const from = Math.max(1, Math.min(this.page() - 3, n - 6));
    return Array.from({ length: Math.min(n, 7) }, (_, i) => from + i);
  });
  readonly end = computed(() => Math.min(this.page() * this.pageSize, this.store.total()));
  private searchTimer: ReturnType<typeof setTimeout> | undefined;
  constructor() {
    const q = this.route.snapshot.queryParamMap;
    if (q.get('status')) this.status.set(q.get('status')!);
    if (q.get('q')) this.search.set(q.get('q')!);
    if (q.get('policy'))
      this.store.detail(q.get('policy')!).subscribe({
        next: (p) => this.selected.set(p),
        error: () => this.toast.set('პოლისი ვერ ჩაიტვირთა.'),
      });
    if (q.has('new')) location.assign('/policies/new');
    this.go(1);
  }
  private query(page: number): PolicyQuery {
    return {
      search: this.search().trim(),
      status: this.status(),
      payment: this.payment(),
      oldest: this.ascending(),
      offset: (page - 1) * this.pageSize,
      limit: this.pageSize,
    };
  }
  go(page: number) {
    this.page.set(page);
    this.store.load(this.query(page));
  }
  onSearch(value: string) {
    this.search.set(value);
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.go(1), 300);
  }
  count(key: string) {
    return key === 'all' ? this.store.allCount() : (this.store.counts()[key] ?? 0);
  }
  setStatus(value: string) {
    this.status.set(value);
    this.go(1);
  }
  reset() {
    this.search.set('');
    this.payment.set('all');
    this.status.set('all');
    this.go(1);
  }
  open(p: Policy) {
    this.returnFocus = document.activeElement as HTMLElement;
    this.selected.set(p);
  }
  closeDetail() {
    this.selected.set(null);
    this.clearQuery();
    this.returnFocus?.focus();
  }
  openNew() {
    location.assign('/policies/new');
  }
  private clearQuery() {
    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }
  exportCsv() {
    this.store.all(this.query(1)).subscribe({
      next: (policies) => this.download(policies),
      error: () => this.toast.set('ფაილი ვერ მომზადდა. სცადე ხელახლა.'),
    });
  }
  private download(policies: Policy[]) {
    const rows = [
      ['პოლისი', 'კლიენტი', 'ავტომობილი', 'ნომერი', 'პრემია', 'გადახდილი', 'სტატუსი'],
      ...policies.map((p) => [
        p.number,
        p.client,
        p.vehicle,
        p.plate,
        formatMoney(p.premium, false),
        formatMoney(p.paid, false),
        this.label(p.status),
      ]),
    ];
    const escape = (v: string | number) =>
      '"' +
      String(v)
        .replace(/^[=+@-]/, (m) => "'" + m)
        .replace(/"/g, '""') +
      '"';
    const content = '\uFEFF' + rows.map((r) => r.map(escape).join(',')).join('\r\n');
    const url = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8;' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = 'aguli-policies.csv';
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    this.toast.set('მიმდინარე ფილტრით შერჩეული პოლისების ფაილი ჩამოიტვირთა.');
  }
}
