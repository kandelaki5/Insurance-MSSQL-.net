import { Component, inject } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DemoStore, statusLabel } from './demo-store';
import { Icon } from './icon';
import { MoneyPipe } from './money';
@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, DecimalPipe, Icon, MoneyPipe],
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
        <p class="eyebrow">ყველაფერი ერთ სივრცეში</p>
        <h1>გამარჯობა, {{ fullName }} <span class="greeting-spark">✦</span></h1>
        <p>დღის საქმეები და შენი პორტფელის მოკლე მიმოხილვა.</p>
      </div>
      @if (store.canWrite()) {
        <a class="button primary" href="/policies/new"><ui-icon name="plus" />ახალი პოლისი</a>
      }
    </div>
    <section class="welcome-banner">
      <div>
        <span class="banner-label"><span class="online-dot"></span>შენი სამუშაო დღე</span>
        <h2>მეტი სიცხადე.<br />უფრო მარტივი მართვა.</h2>
        <p>
          პოლისები, გადახდები და მნიშვნელოვანი<br class="desktop-break" />
          დეტალები — ყოველთვის ხელთ.
        </p>
        <a routerLink="/policies" class="text-link">პოლისებზე გადასვლა<ui-icon name="arrow" /></a>
      </div>
      <div class="banner-art" aria-hidden="true">
        <span class="art-orbit orbit-one"></span><span class="art-orbit orbit-two"></span>
        <div class="art-shield"><ui-icon name="shield" /></div>
        <div class="floating-card float-top">
          <span class="mini-check">✓</span>
          <div>დაცული და მოწესრიგებული<small>შენი სადაზღვევო პორტფელი</small></div>
        </div>
        <div class="floating-card float-bottom">
          <span class="art-dot"></span>ყველაფერი კონტროლქვეშაა
        </div>
      </div>
    </section>
    <section class="metrics" aria-label="ძირითადი მაჩვენებლები">
      <article class="metric">
        <div class="metric-top">
          <span>მოქმედი პოლისების პრემია</span
          ><span class="icon-tile lavender"><ui-icon name="shield" /></span>
        </div>
        <strong>{{ store.premium() | number: '1.0-0' }} <small>₾</small></strong>
        <p>
          <span class="metric-dot purple"></span>{{ store.dashboard()?.portfolio?.active }} მოქმედი
          პოლისი
        </p>
      </article>
      <article class="metric">
        <div class="metric-top">
          <span>მოქმედი პოლისები</span><span class="icon-tile mint"><ui-icon name="wallet" /></span>
        </div>
        <strong>{{ store.dashboard()?.portfolio?.active | number: '1.0-0' }}</strong>
        <p><span class="metric-dot green"></span>მოქმედი პორტფელი</p>
      </article>
      <article class="metric">
        <div class="metric-top">
          <span>დასამტკიცებელი პოლისები</span
          ><span class="icon-tile peach"><ui-icon name="clock" /></span>
        </div>
        <strong>{{ store.dashboard()?.totals?.pending | number: '1.0-0' }}</strong>
        <p>დამტკიცების მოლოდინში</p>
      </article>
      <article class="metric">
        <div class="metric-top">
          <span>გადაცემული პრემია</span
          ><span class="icon-tile blue"><ui-icon name="layers" /></span>
        </div>
        <strong>{{ store.ceded() | number: '1.0-0' }} <small>₾</small></strong>
        <p>წმინდა ჩაწერილი გადაცემა · GEL</p>
      </article>
    </section>
    <div class="dashboard-grid">
      <section class="panel premium-panel">
        <div class="panel-heading">
          <div>
            <h2>პოლისების მდგომარეობა</h2>
            <p>მთლიანი პორტფელის მიმოხილვა</p>
          </div>
          <span class="soft-label">პოლისები</span>
        </div>
        <div class="premium-body">
          <div
            class="donut"
            [style.background]="donut()"
            role="img"
            [attr.aria-label]="'მოქმედია ' + percent() + ' პროცენტი'"
          >
            <div>
              <span>მოქმედია</span><strong>{{ percent() }}<small>%</small></strong
              ><span>ყველა პოლისიდან</span>
            </div>
          </div>
          <div class="premium-legend">
            <div>
              <span class="legend-dot paid"></span>
              <div>
                <span>მოქმედი პოლისები</span
                ><strong>{{ store.dashboard()?.portfolio?.active }} პოლისი</strong>
              </div>
            </div>
            <div>
              <span class="legend-dot unpaid"></span>
              <div>
                <span>სხვა სტატუსი</span
                ><strong
                  >{{
                    (store.dashboard()?.portfolio?.allPolicies ?? 0) -
                      (store.dashboard()?.portfolio?.active ?? 0)
                  }}
                  პოლისი</strong
                >
              </div>
            </div>
            <p><ui-icon name="info" />ფინანსური დეტალები პოლისის ბარათშია.</p>
          </div>
        </div>
      </section>
      <section class="panel attention-panel">
        <div class="panel-heading">
          <div>
            <h2>შენს ყურადღებას ელოდება</h2>
            <p>დღის სამუშაო სია</p>
          </div>
          <span class="count-badge">{{ store.overviewPending() }}</span>
        </div>
        @for (p of store.pendingPreview(); track p.number) {
          <a class="task-row" routerLink="/policies" [queryParams]="{ policy: p.id }"
            ><span class="task-icon"><ui-icon name="clock" /></span>
            <div>
              <strong>{{ p.number }}</strong
              ><span>{{ p.client }}</span
              ><small>პოლისი დასამტკიცებელია</small>
            </div>
            <ui-icon name="arrow"
          /></a>
        }
        <a
          class="text-link task-all"
          routerLink="/policies"
          [queryParams]="{ status: 'pending_approval' }"
          >ყველას ნახვა<ui-icon name="arrow"
        /></a>
      </section>
    </div>
    <section class="panel" style="margin-bottom:24px">
      <div class="panel-heading">
        <div>
          <h2>სამუშაო შეხსენებები</h2>
          <p>მიმდინარე საქმეები და შესაბამისობის საკითხები</p>
        </div>
        <a href="/reference/rates" class="text-link">ვალუტის კურსები<ui-icon name="arrow" /></a>
      </div>
      @for (r of store.dashboard()?.reminders ?? []; track r.href + $index) {
        <a class="task-row" [href]="r.href"
          ><span class="count-badge">{{ r.count }}</span>
          <div>{{ r.label }}</div>
          <ui-icon name="arrow"
        /></a>
      } @empty {
        <p style="padding:0 24px 24px;color:#9b90a8;font-size:11px">
          მიმდინარე შეხსენებები არ არის.
        </p>
      }
    </section>
    <section class="panel recent-panel">
      <div class="panel-heading">
        <div>
          <h2>ბოლო პოლისები</h2>
          <p>ახლახან დამატებული ჩანაწერები</p>
        </div>
        <a class="text-link" routerLink="/policies">ყველა პოლისი<ui-icon name="arrow" /></a>
      </div>
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>პოლისი</th>
              <th>კლიენტი</th>
              <th>ავტომობილი</th>
              <th class="number-cell">პრემია</th>
              <th>სტატუსი</th>
              <th><span class="sr-only">გახსნა</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of store.latest(); track p.number) {
              <tr>
                <td>
                  <a class="policy-link" routerLink="/policies" [queryParams]="{ policy: p.id }">{{
                    p.number
                  }}</a
                  ><small class="cell-sub">ავტოდაზღვევა</small>
                </td>
                <td>
                  <div class="client-cell">
                    <span class="client-avatar" [class.mint-avatar]="$index % 2 === 1">{{
                      p.initials
                    }}</span
                    ><span>{{ p.client }}</span>
                  </div>
                </td>
                <td>
                  {{ p.vehicle }}<small class="cell-sub">{{ p.plate }}</small>
                </td>
                <td class="number-cell">
                  <strong>{{ p.premium | money }} {{ p.currency }}</strong>
                </td>
                <td>
                  <span class="status" [class.pending]="p.status === 'pending_approval'"
                    ><i></i>{{ label(p.status) }}</span
                  >
                </td>
                <td>
                  <a
                    class="icon-button"
                    routerLink="/policies"
                    [queryParams]="{ policy: p.id }"
                    [attr.aria-label]="p.number + ' გახსნა'"
                    ><ui-icon name="arrow"
                  /></a>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </section>
  `,
})
export class Dashboard {
  readonly store = inject(DemoStore);
  readonly fullName =
    document.querySelector('app-root')?.getAttribute('data-full-name') ?? 'მომხმარებელი';
  readonly label = statusLabel;
  constructor() {
    this.store.loadOverview();
  }
  percent() {
    return (
      Math.round(
        ((this.store.dashboard()?.portfolio.active ?? 0) /
          (this.store.dashboard()?.portfolio.allPolicies ?? 1)) *
          100,
      ) || 0
    );
  }
  donut() {
    return (
      'conic-gradient(var(--chart-paid) 0% ' +
      this.percent() +
      '%, var(--chart-track) ' +
      this.percent() +
      '% 100%)'
    );
  }
}
