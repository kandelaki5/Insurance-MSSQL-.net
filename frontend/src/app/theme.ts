import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';

type Theme = 'light' | 'dark';
const storageKey = 'aguli-color-theme';

function savedTheme(): Theme {
  try {
    return localStorage.getItem(storageKey) === 'dark' ? 'dark' : 'light';
  } catch {
    return 'light';
  }
}

function applyTheme(theme: Theme): void {
  document.documentElement.dataset['theme'] = theme;
  document.documentElement.style.colorScheme = theme;
}

// Apply the saved preference before Angular creates the first view.
export function initializeTheme(): void {
  applyTheme(savedTheme());
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly current = signal<Theme>(savedTheme());
  readonly dark = computed(() => this.current() === 'dark');

  constructor() {
    applyTheme(this.current());
    const onStorage = (event: StorageEvent) => {
      if (event.key !== storageKey && event.key !== null) return;
      const theme = savedTheme();
      this.current.set(theme);
      applyTheme(theme);
    };
    window.addEventListener('storage', onStorage);
    inject(DestroyRef).onDestroy(() => window.removeEventListener('storage', onStorage));
  }

  toggle(): void {
    const theme = this.dark() ? 'light' : 'dark';
    this.current.set(theme);
    applyTheme(theme);
    try {
      localStorage.setItem(storageKey, theme);
    } catch {
      // The switch still works for this page if the browser disallows storage.
    }
  }
}
