import { Component, input } from '@angular/core';
const paths: Record<string, string> = {
  moon: 'M20.9 13a9 9 0 0 1-9.9-9.9A9 9 0 1 0 20.9 13Z',
  sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M12 2v2 M12 20v2 M2 12h2 M20 12h2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4',
  logout: 'M9 5H3v14h6 M14 8l4 4-4 4 M7 12h11',
  file: 'M14 2H5v20h14V7z M14 2v5h5 M7 12h8 M7 16h8',
  settings:
    'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M4 4l3 2 M17 18l3 2 M4 20l3-2 M17 6l3-2 M12 1v3 M12 20v3 M1 12h3 M20 12h3',
  claim: 'M14 2H5a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V9z M14 2v7h7 M12 12v4 M12 19h.01',
  grid: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  shield: 'M12 3 3 7v5c0 5 9 9 9 9s9-4 9-9V7z M8 12l3 3 5-6',
  users:
    'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8 M20 21v-2a4 4 0 0 0-3-4 M17 3a4 4 0 0 1 0 8',
  wallet: 'M21 8H5a2 2 0 0 1 0-4h14v4 M3 6v13a2 2 0 0 0 2 2h16V8 M21 12h-5v5h5',
  heart:
    'M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1.1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8Z',
  layers: 'm12 3 10 6-10 6L2 9z M2 14l10 6 10-6 M2 18l10 5 10-5',
  chart: 'M3 3v18h18 M7 16v-5 M12 16V7 M17 16V4',
  search: 'M21 21l-5-5 M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
  plus: 'M12 5v14 M5 12h14',
  arrow: 'M4 12h16 M14 6l6 6-6 6',
  down: 'm6 9 6 6 6-6',
  download: 'M12 3v12 M7 10l5 5 5-5 M4 16v5h16v-5',
  clock: 'M12 7v5l3 2 M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0',
  check: 'm5 12 4 4L19 6',
  close: 'm6 6 12 12 M6 18 18 6',
  car: 'm5 7 2-4h10l2 4 M3 7h18v11H3z M5 18v3 M19 18v3 M6 12h2 M16 12h2',
  calendar: 'M8 2v4 M16 2v4 M3 10h18 M3 4h18v18H3z',
  sort: 'M8 3v18 M4 7l4-4 4 4 M16 21V3 M12 17l4 4 4-4',
  menu: 'M3 6h18 M3 12h18 M3 18h18',
  info: 'M12 11v6 M12 7h.01 M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0',
};
@Component({
  selector: 'ui-icon',
  template: `<svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.7"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <path [attr.d]="path()" />
  </svg>`,
  styles: [
    ':host{display:inline-flex;width:20px;height:20px;flex-shrink:0}svg{width:100%;height:100%}',
  ],
})
export class Icon {
  name = input.required<string>();
  path() {
    return paths[this.name()] ?? paths['shield'];
  }
}
