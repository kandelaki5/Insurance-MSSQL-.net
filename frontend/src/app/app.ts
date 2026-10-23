import { Component, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Icon } from './icon';
import { ThemeService } from './theme';
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Icon],
  template: ` <a class="skip-link" href="#main">მთავარ შინაარსზე გადასვლა</a>
    <aside class="sidebar" [class.mobile-open]="menuOpen()">
      <a class="brand" href="/"
        ><span class="brand-mark"><i></i><i></i><i></i></span
        ><span>აგული<small>სადაზღვევო სივრცე</small></span></a
      >
      <div class="workspace">
        <span class="workspace-icon">ა</span>
        <div>ჩემი სამუშაო სივრცე<small>.NET · MSSQL</small></div>
        <span class="online-dot"></span>
      </div>
      <p class="nav-caption">სამუშაო მაგიდა</p>
      <nav class="native-nav" aria-label="მთავარი ნავიგაცია">
        @for (item of links; track $index) {
          @if (item.group) {
            <div class="nav-group">{{ item.title }}</div>
          } @else {
            <a
              [href]="item.href"
              [class.active]="active(item.href)"
              [attr.aria-current]="active(item.href) ? 'page' : null"
              ><ui-icon [name]="icon(item.href)" />{{ item.title }}</a
            >
          }
        }
      </nav>
      <div class="sidebar-bottom">
        <div class="profile">
          <span class="avatar">{{ initials }}</span>
          <div>
            {{ fullName }}<small>{{ role }}</small>
          </div>
          <button class="logout icon-button" aria-label="გასვლა" title="გასვლა" (click)="logout()">
            <ui-icon name="logout" />
          </button>
        </div>
      </div>
    </aside>
    @if (menuOpen()) {
      <button
        class="mobile-shade"
        aria-label="მენიუს დახურვა"
        (click)="menuOpen.set(false)"
      ></button>
    }
    <div class="app-body">
      <header class="topbar">
        <div class="topbar-left">
          <button
            class="icon-button menu-toggle"
            aria-label="მენიუს გახსნა"
            (click)="menuOpen.set(!menuOpen())"
          >
            <ui-icon name="menu" /></button
          ><span>ჩემი სამუშაო სივრცე</span><span class="top-separator">/</span
          ><strong>{{ title }}</strong>
        </div>
        <div class="topbar-right">
          <button
            type="button"
            class="theme-toggle"
            [attr.aria-label]="theme.dark() ? 'ღია თემაზე გადასვლა' : 'მუქ თემაზე გადასვლა'"
            [attr.title]="theme.dark() ? 'ღია თემაზე გადასვლა' : 'მუქ თემაზე გადასვლა'"
            [attr.aria-pressed]="theme.dark()"
            (click)="theme.toggle()"
          >
            <ui-icon [name]="theme.dark() ? 'sun' : 'moon'" />
            <span>{{ theme.dark() ? 'ღია თემა' : 'მუქი თემა' }}</span>
          </button>
          <span class="demo-pill"><span></span>დემოს სამუშაო სივრცე</span
          ><span class="top-date">{{ today }}</span
          ><span class="avatar small-avatar">{{ initials }}</span>
        </div>
      </header>
      <main id="main" tabindex="-1"><router-outlet /></main>
      <footer class="app-footer">
        <span>აგული · სადაზღვევო პროგრამა</span><span>.NET და MSSQL · სამუშაო მონაცემები</span>
      </footer>
    </div>`,
})
export class App {
  readonly theme = inject(ThemeService);
  readonly menuOpen = signal(false);
  private root = document.querySelector('app-root')!;
  readonly fullName = this.root.getAttribute('data-full-name') ?? 'მომხმარებელი';
  readonly role = this.root.getAttribute('data-role-name') ?? '';
  readonly initials = this.fullName
    .split(' ')
    .slice(0, 2)
    .map((n) => n[0])
    .join('');
  readonly today = this.georgianDate();
  readonly title = document.title.split(' — ')[0];
  readonly links = Array.from(
    document.querySelectorAll('#server-navigation a,#server-navigation .nav-group'),
  ).map((e) => ({
    group: !e.hasAttribute('href'),
    href: e.getAttribute('href') ?? '/',
    title: e.textContent?.trim() ?? '',
  }));
  active(href: string) {
    return href === '/'
      ? location.pathname === '/'
      : location.pathname === href ||
          (location.pathname.startsWith(href + '/') &&
            !this.links.some(
              (l) =>
                l.href !== href &&
                l.href.startsWith(href + '/') &&
                location.pathname.startsWith(l.href),
            ));
  }
  icon(href: string) {
    if (href === '/') return 'grid';
    if (href.includes('claim')) return 'claim';
    if (href.includes('policies')) return 'shield';
    if (href.includes('parties') || href.includes('users') || href.includes('intermediaries'))
      return 'users';
    if (href.includes('payments')) return 'wallet';
    if (href.includes('reinsurance')) return 'layers';
    if (href.includes('accounting') || href.includes('finance') || href.includes('report'))
      return 'chart';
    if (href.includes('settings')) return 'settings';
    return 'file';
  }
  georgianDate() {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone: 'Asia/Tbilisi',
      day: 'numeric',
      month: 'numeric',
      year: 'numeric',
    }).formatToParts(new Date());
    const get = (key: string) => parts.find((p) => p.type === key)?.value ?? '';
    const months = [
      'იანვარი',
      'თებერვალი',
      'მარტი',
      'აპრილი',
      'მაისი',
      'ივნისი',
      'ივლისი',
      'აგვისტო',
      'სექტემბერი',
      'ოქტომბერი',
      'ნოემბერი',
      'დეკემბერი',
    ];
    return get('day') + ' ' + months[Number(get('month')) - 1] + ', ' + get('year');
  }
  logout() {
    document.querySelector<HTMLFormElement>('#server-logout')?.requestSubmit();
  }
}
