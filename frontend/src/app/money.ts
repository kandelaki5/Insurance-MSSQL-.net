import { Pipe, PipeTransform } from '@angular/core';

/** An exact amount as /api/v1 gives it: whole minor units as a decimal string (ADR 0004). */
export interface Money {
  minor: string;
  currency: string;
  scale: number;
}

const minorOf = (m: Money) => BigInt(m.minor);

export const isZero = (m: Money | null | undefined) => !!m && minorOf(m) === 0n;
export const isPositive = (m: Money | null | undefined) => !!m && minorOf(m) > 0n;

/** The amount written from its digits, never through a floating-point number: 123450 at scale 2 → "1,234.50". */
export function formatMoney(m: Money, grouping = true): string {
  const negative = m.minor.startsWith('-');
  const digits = (negative ? m.minor.slice(1) : m.minor).padStart(m.scale + 1, '0');
  const whole = digits.slice(0, digits.length - m.scale);
  const part = digits.slice(digits.length - m.scale);
  const grouped = grouping ? whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',') : whole;
  return (negative ? '-' : '') + grouped + (m.scale ? '.' + part : '');
}

/** The sum of amounts of one currency, exactly. */
export function sumMoney(items: Money[], currency: string, scale = 2): Money {
  return {
    minor: items.reduce((s, m) => s + minorOf(m), 0n).toString(),
    currency,
    scale,
  };
}

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(value: Money | null | undefined): string {
    return value ? formatMoney(value) : '';
  }
}
