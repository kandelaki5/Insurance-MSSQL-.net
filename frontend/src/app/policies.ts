import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { DemoStore, Policy, statusLabel } from './demo-store';
import { Icon } from './icon';
import { PolicyDetail } from './policy-detail';

@Component({
  selector: 'app-policies',
  imports: [DatePipe, DecimalPipe, FormsModule, Icon, PolicyDetail],
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
          პოლისები <span class="heading-count">{{ store.policies().length }}</span>
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
          <span>მოქმედი პოლისები</span><strong>{{ store.active().length }}</strong>
        </div>
      </div>
      <div>
        <span class="icon-tile peach"><ui-icon name="clock" /></span>
        <div>
          <span>დასამტკიცებელი</span><strong>{{ store.pending().length }}</strong>
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
            (ngModelChange)="search.set($event); page.set(1)"
        /></label>
        <div class="filter-right">
          <label class="select-field"
            ><ui-icon name="wallet" /><select
              aria-label="გადახდის ფილტრი"
              [ngModel]="payment()"
              (ngModelChange)="payment.set($event); page.set(1)"
            >
              <option value="all">ყველა გადახდა</option>
              <option value="unpaid">გადაუხდელი ნაშთით</option>
              <option value="paid">სრულად გადახდილი</option>
            </select></label
          ><button
            class="button sort-button"
            (click)="ascending.set(!ascending()); page.set(1)"
            [attr.aria-label]="ascending() ? 'ჯერ ახალი პოლისები' : 'ჯერ ძველი პოლისები'"
          >
            <ui-icon name="sort" />{{ ascending() ? 'ჯერ ძველი' : 'ჯერ ახალი' }}
          </button>
        </div>
      </div>
      @if (search() || payment() !== 'all' || status() !== 'all') {
        <div class="filter-result">
          <button class="text-link" (click)="store.load(search()); page.set(1)">ბაზაში ძიება</button
          ><span>მოიძებნა {{ filtered().length }} პოლისი</span
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
                  <strong>{{ p.premium | number: '1.2-2' }} {{ p.currency }}</strong
                  ><small
                    class="cell-sub"
                    [class.green-text]="p.status === 'active' && p.outstanding === 0"
                  >
                    @if (p.outstanding === null) {
                      დამტკიცების შემდეგ
                    } @else if (p.outstanding === 0) {
                      სრულად გადახდილი
                    } @else {
                      ნაშთი: {{ p.outstanding ?? 0 | number: '1.2-2' }} {{ p.currency }}
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
          >ნაჩვენებია {{ filtered().length ? (page() - 1) * pageSize + 1 : 0 }}–{{ end() }} /
          {{ filtered().length }} პოლისი</span
        >
        <div>
          <button
            class="page-button"
            [disabled]="page() === 1"
            aria-label="წინა გვერდი"
            (click)="page.set(page() - 1)"
          >
            ←
          </button>
          @for (n of pages(); track n) {
            <button
              class="page-button"
              [class.current]="page() === n"
              [attr.aria-current]="page() === n ? 'page' : null"
              [attr.aria-label]="'გვერდი ' + n"
              (click)="page.set(n)"
            >
              {{ n }}
            </button>
          }
          <button
            class="page-button"
            [disabled]="page() >= pages().length"
            aria-label="შემდეგი გვერდი"
            (click)="page.set(page() + 1)"
          >
            →
          </button>
        </div>
      </div>
    </section>
    <p class="page-hint">
      <ui-icon name="info" />ბოლო 200 პოლისი. სხვა ჩანაწერის მოსაძებნად გამოიყენე ბაზაში ძიება.
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
  readonly search = signal('');
  readonly status = signal('all');
  readonly payment = signal('all');
  readonly ascending = signal(false);
  readonly page = signal(1);
  readonly selected = signal<Policy | null>(null);
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
  readonly filtered = computed(() => {
    const q = this.search().trim().toLocaleLowerCase();
    return this.store
      .policies()
      .filter(
        (p) =>
          (this.status() === 'all' || p.status === this.status()) &&
          (!q ||
            [p.number, p.client, p.vehicle, p.plate].some((v) =>
              v.toLocaleLowerCase().includes(q),
            )) &&
          (this.payment() === 'all' ||
            (p.status === 'active' &&
              (this.payment() === 'paid' ? p.outstanding === 0 : (p.outstanding ?? 0) > 0))),
      )
      .sort((a, b) =>
        this.ascending() ? a.number.localeCompare(b.number) : b.number.localeCompare(a.number),
      );
  });
  readonly visible = computed(() =>
    this.filtered().slice((this.page() - 1) * this.pageSize, this.page() * this.pageSize),
  );
  readonly pages = computed(() =>
    Array.from(
      { length: Math.max(1, Math.ceil(this.filtered().length / this.pageSize)) },
      (_, i) => i + 1,
    ),
  );
  readonly end = computed(() => Math.min(this.page() * this.pageSize, this.filtered().length));
  constructor() {
    const q = this.route.snapshot.queryParamMap;
    if (q.get('status')) this.status.set(q.get('status')!);
    if (q.get('policy'))
      this.store
        .detail(q.get('policy')!)
        .subscribe({
          next: (p) => this.selected.set(p),
          error: () => this.toast.set('პოლისი ვერ ჩაიტვირთა.'),
        });
    if (q.has('new')) location.assign('/policies/new');
  }
  count(key: string) {
    return key === 'all'
      ? this.store.policies().length
      : this.store.policies().filter((p) => p.status === key).length;
  }
  setStatus(value: string) {
    this.status.set(value);
    this.page.set(1);
  }
  reset() {
    this.search.set('');
    this.payment.set('all');
    this.status.set('all');
    this.page.set(1);
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
    const rows = [
      ['პოლისი', 'კლიენტი', 'ავტომობილი', 'ნომერი', 'პრემია', 'გადახდილი', 'სტატუსი'],
      ...this.filtered().map((p) => [
        p.number,
        p.client,
        p.vehicle,
        p.plate,
        p.premium,
        p.paid,
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
