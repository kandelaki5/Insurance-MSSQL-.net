import {
  Component,
  ElementRef,
  HostListener,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { DemoStore, Policy, statusLabel } from './demo-store';
import { Icon } from './icon';
@Component({
  selector: 'policy-detail',
  imports: [Icon, DatePipe, DecimalPipe],
  template: `<div class="overlay" (click)="close.emit()"></div>
    <section
      #panel
      class="detail-panel"
      role="dialog"
      aria-modal="true"
      aria-labelledby="detail-title"
      tabindex="-1"
    >
      <header class="detail-header">
        <div>
          <span class="eyebrow">პოლისის ბარათი</span>
          <h2 id="detail-title">{{ policy().number }}</h2>
        </div>
        <button class="icon-button" aria-label="ბარათის დახურვა" (click)="close.emit()">
          <ui-icon name="close" />
        </button>
      </header>
      <div class="detail-content">
        @if (error()) {
          <div class="workspace-error" role="alert">{{ error() }}</div>
        }
        @if (data(); as p) {
          <span class="status" [class.pending]="p.status !== 'active'"
            ><i></i>{{ label(p.status) }}</span
          >
          <div class="detail-client">
            <span class="client-avatar large-avatar"><ui-icon name="users" /></span>
            <div>
              <h3>{{ p.client }}</h3>
              <p>{{ p.product }}</p>
            </div>
          </div>
          <div class="vehicle-card">
            <span class="icon-tile lavender"><ui-icon name="car" /></span>
            <div>
              <strong>{{ p.vehicle || '—' }}</strong>
            </div>
          </div>
          <h3 class="detail-section-title">მოქმედების პერიოდი</h3>
          <div class="date-range">
            <div>
              <span>დაწყება</span><strong>{{ p.from | date: 'dd.MM.yyyy' }}</strong>
            </div>
            <ui-icon name="arrow" />
            <div>
              <span>დასრულება</span><strong>{{ p.to | date: 'dd.MM.yyyy' }}</strong>
            </div>
          </div>
          <h3 class="detail-section-title">პრემია და მიმდინარე ნაშთი</h3>
          <div class="finance-summary">
            <div>
              <span>სრული პრემია</span
              ><strong>{{ p.premium | number: '1.2-2' }} {{ p.currency }}</strong>
            </div>
            <div>
              <span>მიმდინარე გადაუხდელი</span
              ><strong>{{ p.outstanding | number: '1.2-2' }} {{ p.currency }}</strong>
            </div>
            <div>
              <span>ვადაგადაცილებული</span
              ><strong>{{ p.overdue | number: '1.2-2' }} {{ p.currency }}</strong>
            </div>
            @if (p.currency !== 'GEL') {
              <div>
                <span>ნაშთის GEL შეფასება</span
                ><strong>{{ p.outstandingGel | number: '1.2-2' }} ₾</strong>
              </div>
            }
            @if (p.overpaid) {
              <div>
                <span>ზედმეტად ჩათვლილი</span
                ><strong>{{ p.overpaid | number: '1.2-2' }} {{ p.currency }}</strong>
              </div>
            }
          </div>
          <p class="info-note">
            <ui-icon name="info" />ნაშთი და ვადაგადაცილება დათვლილია
            {{ p.asOf | date: 'dd.MM.yyyy' }} მდგომარეობით.
          </p>
          @if (p.cessions.length) {
            <div class="ri-card">
              <div class="ri-title">
                <ui-icon name="layers" />
                <h3>გადაზღვევა</h3>
              </div>
              @for (c of p.cessions; track $index) {
                <p>
                  <a [href]="'/reinsurance/treaties/' + c.treatyId"
                    >{{ c.treatyNumber }} · {{ c.treatyName }}</a
                  >
                </p>
                <div class="ri-split">
                  <div>
                    <span>წილი · {{ c.sharePercent }}%</span
                    ><strong>{{ c.amountGel | number: '1.2-2' }} ₾</strong>
                  </div>
                  <div>
                    <span>{{ c.isCancellation ? 'გაუქმება' : 'დარიცხვა' }}</span
                    ><strong>{{ c.bookedOn | date: 'dd.MM.yyyy' }}</strong>
                  </div>
                </div>
              }
            </div>
          }
          <h3 class="detail-section-title">გადახდის გრაფიკი</h3>
          <div class="finance-summary">
            @for (i of p.schedule; track $index) {
              <div>
                <span>{{ i.dueOn | date: 'dd.MM.yyyy' }}</span
                ><strong
                  >{{ i.outstanding | number: '1.2-2' }} / {{ i.amount | number: '1.2-2' }}
                  {{ p.currency }}</strong
                >
              </div>
            } @empty {
              <div><span>გრაფიკი ჯერ არ არის.</span></div>
            }
          </div>
          <div style="display:flex;gap:10px;margin-top:24px;flex-wrap:wrap">
            <a class="button primary" [href]="'/policies/' + p.id"
              >სრული ბარათი და მოქმედებები<ui-icon name="arrow" /></a
            ><a class="button secondary" [href]="'/print/policies/' + p.id">ბეჭდვა</a>
          </div>
        } @else if (!error()) {
          <p role="status">ბარათი იტვირთება…</p>
        }
      </div>
    </section>`,
})
export class PolicyDetail {
  policy = input.required<Policy>();
  close = output<void>();
  label = statusLabel;
  panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private store = inject(DemoStore);
  data = signal<any>(null);
  error = signal('');
  constructor() {
    effect((onCleanup) => {
      this.data.set(null);
      this.error.set('');
      const sub = this.store
        .detail(this.policy().id)
        .subscribe({
          next: (p) => this.data.set(p),
          error: () => this.error.set('პოლისის ბარათი ვერ ჩაიტვირთა.'),
        });
      onCleanup(() => sub.unsubscribe());
    });
    afterNextRender(() => this.panel().nativeElement.focus());
  }
  @HostListener('keydown', ['$event']) key(event: KeyboardEvent) {
    if (event.key === 'Escape') this.close.emit();
    if (event.key === 'Tab') {
      const els = Array.from(
        this.panel().nativeElement.querySelectorAll<HTMLElement>('button,a[href]'),
      );
      const first = els[0],
        last = els[els.length - 1];
      if (
        event.shiftKey &&
        (document.activeElement === first || document.activeElement === this.panel().nativeElement)
      ) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    }
  }
}
