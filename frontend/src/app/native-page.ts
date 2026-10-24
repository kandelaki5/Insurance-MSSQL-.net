import { Component, ElementRef, afterNextRender, viewChild } from '@angular/core';
@Component({
  selector: 'native-page',
  template: `<div class="native-breadcrumb">
      <a href="/">მთავარი</a><span>/</span><a [href]="sectionHref">{{ section }}</a>
    </div>
    <section #content></section>`,
})
export class NativePage {
  content = viewChild.required<ElementRef<HTMLElement>>('content');
  section = 'სამუშაო სივრცე';
  sectionHref = '/';
  constructor() {
    const current = document.querySelector<HTMLAnchorElement>(
      '#server-navigation a[href="' + location.pathname.split('/').slice(0, 2).join('/') + '"]',
    );
    this.section = current?.textContent?.trim() ?? 'სამუშაო სივრცე';
    this.sectionHref = current?.getAttribute('href') ?? '/';
    afterNextRender(() => {
      const content = document.getElementById('server-content');
      if (content) {
        this.content().nativeElement.appendChild(content);
        content.hidden = false;
      }
    });
  }
}
