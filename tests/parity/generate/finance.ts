// Parity cases for the finance core: runs the old TypeScript functions of src/finance (Insurance-Software at commit
// 17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37) on fixed, generated inputs and writes their inputs and outputs to
// tests/parity/cases/finance-*.json. The C# tests (tests/Insurance.Domain.Tests/Finance/Parity*.cs) require the new code
// to give exactly the same results.
//
// Run: LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/finance.ts
// Deterministic: a seeded generator (mulberry32) and hand-picked boundaries; the same old code gives the same files.
// Values: amounts and dates as the old code holds them; a bigint is written as its decimal text; an error as { error }.

import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const LEGACY = resolve(process.env.LEGACY_DIR ?? '/home/user/legacy-run');
const OUT = resolve(dirname(fileURLToPath(import.meta.url)), '..', 'cases');
const load = (module: string) => import(pathToFileURL(join(LEGACY, 'src', 'finance', module)).href);

const money = await load('money.ts');
const dates = await load('dates.ts');
const ep = await load('earned-premium.ts');
const commission = await load('commission.ts');
const reserves = await load('reserves.ts');
const receivable = await load('receivable.ts');
const totalLoss = await load('total-loss.ts');
const deductible = await load('deductible.ts');
const plan = await load('plan.ts');
const cancelPreview = await load('cancel-preview.ts');
const impairment = await load('impairment.ts');
const paymentAuth = await load('payment-authorization.ts');
const reinsurance = await load('reinsurance.ts');
const subrogation = await load('subrogation.ts');
const report = await load('subrogation-report.ts');

type Any = any; // eslint-disable-line @typescript-eslint/no-explicit-any

// ---------- deterministic randomness ----------
function mulberry32(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
type Rnd = () => number;
const int = (r: Rnd, a: number, b: number) => a + Math.floor(r() * (b - a + 1));
const pick = <T>(r: Rnd, xs: readonly T[]): T => xs[Math.floor(r() * xs.length)]!;
const chance = (r: Rnd, p: number) => r() < p;
const shuffle = <T>(r: Rnd, xs: readonly T[]): T[] => {
  const a = [...xs];
  for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(r() * (i + 1)); [a[i], a[j]] = [a[j]!, a[i]!]; }
  return a;
};
const randDate = (r: Rnd, from: string, to: string) => dates.addDays(from, int(r, 0, dates.daysBetween(from, to)));
const CURS = ['GEL', 'USD', 'EUR'] as const;

// ---------- output ----------
const json = (value: unknown) => JSON.stringify(value, (_k, v) => (typeof v === 'bigint' ? v.toString() : v));
function run(fn: () => unknown) {
  try { return { out: fn() }; } catch (e) { return { error: (e as Error).message }; }
}
class File {
  cases: Record<string, unknown[]> = {};
  name: string;
  constructor(name: string) { this.name = name; }
  add(fn: string, input: unknown, compute: () => unknown) {
    (this.cases[fn] ??= []).push({ in: input, ...run(compute) });
  }
  write() {
    mkdirSync(OUT, { recursive: true });
    const counts = Object.fromEntries(Object.entries(this.cases).map(([k, v]) => [k, v.length]));
    // One case per line, so a change in the old code's output shows as a readable diff.
    const head = json({ source: 'Insurance-Software 17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37 src/finance', generator: 'tests/parity/generate/finance.ts', counts });
    const body = Object.entries(this.cases).map(([fn, list]) => `${JSON.stringify(fn)}: [\n${list.map((c) => json(c)).join(',\n')}\n]`).join(',\n');
    writeFileSync(join(OUT, this.name), `${head.slice(0, -1)},\n"cases": {\n${body}\n}}\n`);
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    console.log(`${this.name}: ${total} cases`);
    return total;
  }
}
let grandTotal = 0;

// ---------- the rate table (fictional NBG rates), shared by every file through finance-rates.json ----------
const RATE_FROM = '2023-01-01';
const RATE_TO = '2028-12-31';
const rateTable: Record<string, Record<string, number>> = { USD: {}, EUR: {} };
for (let d = RATE_FROM; d <= RATE_TO; d = dates.addDays(d, 1)) {
  const n = dates.dayNumber(d);
  if (n % 53 !== 0) rateTable.USD![d] = Number(((24000 + ((n * 7919) % 6000)) / 10000).toFixed(4));
  if (n % 59 !== 0) {
    rateTable.EUR![d] = n % 7 === 0
      ? Number(((2800000 + ((n * 104729) % 700000)) / 1000000).toFixed(6)) // six decimals on some days
      : Number(((28000 + ((n * 104729) % 7000)) / 10000).toFixed(4));
  }
}
const rates = (currency: string, date: string) => rateTable[currency]?.[date] ?? 0;
mkdirSync(OUT, { recursive: true });
writeFileSync(join(OUT, 'finance-rates.json'), `{"source": "fictional rates for the parity cases; a missing day has no rate (0)",\n"rates": {\n${Object.entries(rateTable).map(([c, t]) => `${JSON.stringify(c)}: ${JSON.stringify(t)}`).join(',\n')}\n}}\n`);

// ---------- shapes ----------
const tx = (minor: number, currency: string, bookedOn: string, coverFrom: string, coverTo: string): Any => ({ amount: { minor, currency }, bookedOn, coverFrom, coverTo });
const exactOf = (t: Any): Any => ({ minor: BigInt(t.amount.minor), bookedOn: t.bookedOn, coverFrom: t.coverFrom, coverTo: t.coverTo, ...(t.cancels ? { cancels: exactOf(t.cancels) } : {}) });
const exactShape = (t: Any): Any => ({ minor: t.minor.toString(), bookedOn: t.bookedOn, coverFrom: t.coverFrom, coverTo: t.coverTo, ...(t.cancels ? { cancels: exactShape(t.cancels) } : {}) });
const clone = <T>(v: T): T => (v === undefined ? v : JSON.parse(JSON.stringify(v)));

function randomAmount(r: Rnd): number {
  const kind = r();
  if (kind < 0.1) return int(r, 1, 999);
  if (kind < 0.15) return -int(r, 1, 100000);
  if (kind < 0.2) return pick(r, [36500, 36600, 120000, 365000, 1, 2, 3, 100001, 9125]);
  return int(r, 1000, 50_000_000);
}
function randomPolicy(r: Rnd, currency?: string, from = '2024-01-01', to = '2027-06-30'): Any {
  const cur = currency ?? pick(r, CURS);
  const coverFrom = randDate(r, from, to);
  const length = chance(r, 0.6) ? pick(r, [365, 366, 365, 184, 181, 90, 31, 30, 1, 2]) : int(r, 1, 800);
  const coverTo = chance(r, 0.3) ? dates.addMonths(coverFrom, 12) : dates.addDays(coverFrom, length);
  const bookedOn = chance(r, 0.7) ? coverFrom : dates.addDays(coverFrom, int(r, -15, 60));
  return tx(randomAmount(r), cur, bookedOn, coverFrom, coverTo);
}
/** A policy's ledger: the issue, endorsements, and maybe a cancellation, its reversal and a cancellation brought forward. */
function randomLedger(r: Rnd, currency?: string, from?: string, to?: string): Any[] {
  const issue = randomPolicy(r, currency, from, to);
  const rows: Any[] = [issue];
  const span = dates.daysBetween(issue.coverFrom, issue.coverTo);
  for (let k = int(r, 0, 2); k > 0; k--) {
    if (span < 2) break;
    const start = dates.addDays(issue.coverFrom, int(r, 1, span - 1));
    rows.push(tx(chance(r, 0.5) ? -int(r, 1, Math.max(1, Math.abs(issue.amount.minor) >> 2)) : int(r, 1, 100000), issue.amount.currency, chance(r, 0.8) ? start : dates.addDays(start, int(r, 0, 20)), start, issue.coverTo));
  }
  if (chance(r, 0.5)) {
    const on = dates.addDays(issue.coverFrom, int(r, -10, span + 5));
    const c = ep.cancellationOf(issue, on);
    if (c) {
      rows.push(c);
      if (chance(r, 0.4)) {
        rows.push(ep.reversalOf(c, chance(r, 0.7) ? c.bookedOn : dates.addDays(c.bookedOn, int(r, 0, 30))));
        const earlier = ep.cancellationOf(issue, dates.addDays(issue.coverFrom, int(r, 0, span)));
        if (earlier && chance(r, 0.6)) rows.push(earlier);
      }
    }
  }
  return rows;
}
const randomAsOf = (r: Rnd, t: Any) => pick(r, [
  t.coverFrom, t.coverTo, t.bookedOn, dates.addDays(t.coverFrom, -1), dates.addDays(t.coverTo, -1),
  randDate(r, dates.addDays(t.coverFrom, -40), dates.addDays(t.coverTo, 40)), randDate(r, dates.addDays(t.coverFrom, -40), dates.addDays(t.coverTo, 40)),
]);

// ======================================================================= money
{
  const f = new File('finance-money.json');
  const r = mulberry32(101);
  // Amounts with at most two decimals, as typed: there the JS double product amount × 100 rounds as the exact decimal
  // product does. (A third decimal at a half, like 2.675, rounds down in JS and up in C# decimal — see the port's report.)
  const amounts = [0, 0.01, -0.01, 0.1, 0.2, 0.3, 1200, 91.25, 120.88, -468, 0.07, 1.15, 999999999.99, -999999999.99, 12345678.9, 0.5, -0.5, 1.5, -1.5];
  for (let i = 0; i < 300; i++) amounts.push(int(r, -10_000_000_00, 10_000_000_00) / 100);
  for (const amount of amounts) { const currency = pick(r, CURS); f.add('money', { amount, currency }, () => money.money(amount, currency)); }
  const mulDivCases: [number, number, number][] = [[1, 1, 2], [-1, 1, 2], [3, 1, 2], [-3, 1, 2], [1, -1, 2], [1, 1, -2], [-1, -1, -2], [5, 1, 0], [0, 7, 3], [120000, 90, 365], [-46800, 181, 341], [36500, 1, 1], [9007199254740, 999, 1000], [1, 1_000_000, 3], [2, 1, 4], [6, 1, 4]];
  for (let i = 0; i < 400; i++) {
    const den = chance(r, 0.1) ? pick(r, [2, -2, 4, 100, 10000, 1000000]) : int(r, -1_000_000, 1_000_000) || 1;
    const num = int(r, -1_000_000, 1_000_000);
    const minor = chance(r, 0.2) ? int(r, -100, 100) : int(r, -1_000_000_000_000, 1_000_000_000_000);
    mulDivCases.push([minor, num, den]);
  }
  // Only results a JS number holds exactly (|result| ≤ 2^53 − 1): beyond, Number() of the old code rounds the exact result.
  const safe = ([m, n, d]: [number, number, number]) => d === 0 || (BigInt(m) * BigInt(n) / BigInt(d) < 2n ** 53n && BigInt(m) * BigInt(n) / BigInt(d) > -(2n ** 53n));
  for (const [minor, numerator, denominator] of mulDivCases.filter(safe)) f.add('mulDiv', { minor, numerator, denominator }, () => money.mulDiv(minor, numerator, denominator));
  for (let i = 0; i < 300; i++) {
    const amount = { minor: int(r, -100_000_000, 100_000_000), currency: pick(r, CURS) };
    const date = randDate(r, '2024-01-01', '2027-12-31');
    f.add('toGel', { amount, date }, () => money.toGel(amount, date, rates));
  }
  for (let i = 0; i < 300; i++) {
    const gelMinor = int(r, -100_000_000, 100_000_000);
    const currency = pick(r, CURS);
    const date = randDate(r, '2024-01-01', '2027-12-31');
    f.add('fromGel', { gelMinor, currency, date }, () => money.fromGel(gelMinor, currency, date, rates));
  }
  grandTotal += f.write();
}

// ======================================================================= dates
{
  const f = new File('finance-dates.json');
  const r = mulberry32(202);
  const ds = ['1900-01-01', '1970-01-01', '2000-02-29', '2024-02-29', '2026-12-31', '2100-12-31', '0001-01-01', '9999-12-31', '1969-12-31', '2016-01-07'];
  for (let i = 0; i < 300; i++) ds.push(randDate(r, '1900-01-01', '2100-12-31'));
  for (const date of ds) f.add('dayNumber', { date }, () => dates.dayNumber(date));
  const texts = ['2026-02-30', '2024-02-29', '2026-02-29', '2100-02-29', '2000-02-29', '1900-02-29', '0000-02-29', '0000-01-01', '9999-12-31', '2026-1-01', '2026-01-01 ', ' 2026-01-01', '2026-01-01\n', '２０２６-01-01', '+020206-04-09', '20206-04-09', '2026-00-10', '2026-13-01', '2026-12-32', '2026-04-31', '2026-06-31', '2026-06-30', '', 'abcd-ef-gh', '2026/01/01', '1899-12-31', '1900-01-01', '2100-12-31', '2101-01-01', '2026-04-09', '15.10.2026', '2026-10-15T00:00:00Z', '2026-02-28', '٢٠٢٦-01-01'];
  for (let i = 0; i < 500; i++) {
    const y = String(int(r, 1850, 2150)).padStart(4, '0');
    const m = String(int(r, 0, 13)).padStart(2, '0');
    const d = String(int(r, 0, 32)).padStart(2, '0');
    texts.push(`${y}-${m}-${d}`);
  }
  for (const value of texts) {
    f.add('isIsoDate', { value }, () => dates.isIsoDate(value));
    f.add('isPlausibleDate', { value }, () => dates.isPlausibleDate(value));
  }
  for (let i = 0; i < 300; i++) { const from = randDate(r, '1990-01-01', '2060-12-31'); const to = randDate(r, '1990-01-01', '2060-12-31'); f.add('daysBetween', { from, to }, () => dates.daysBetween(from, to)); }
  for (let i = 0; i < 300; i++) { const date = randDate(r, '1990-01-01', '2060-12-31'); const days = int(r, -1000, 1000); f.add('addDays', { date, days }, () => dates.addDays(date, days)); }
  const monthEnds: string[] = [];
  for (const y of [2023, 2024, 2025, 2100, 2000, 1900]) for (let m = 1; m <= 12; m++) for (const d of [28, 29, 30, 31]) { const s = `${y}-${String(m).padStart(2, '0')}-${d}`; if (dates.isIsoDate(s)) monthEnds.push(s); }
  for (const date of monthEnds) for (const months of [1, -1, 12, -12, 13, 2, 3, -3, 6, 11]) f.add('addMonths', { date, months }, () => dates.addMonths(date, months));
  for (let i = 0; i < 300; i++) { const date = randDate(r, '1990-01-01', '2060-12-31'); const months = int(r, -40, 40); f.add('addMonths', { date, months }, () => dates.addMonths(date, months)); }
  for (const [date, months] of [['0100-01-15', -1], ['0101-03-31', -13], ['1999-12-15', 1], ['0001-01-31', 1]] as const) f.add('addMonths', { date, months }, () => dates.addMonths(date, months));
  grandTotal += f.write();
}

// ======================================================================= earned premium
{
  const f = new File('finance-earned-premium.json');
  const r = mulberry32(303);
  // Hand-picked: the docs/04 example and the report rules.
  const policy = tx(120000, 'USD', '2026-01-01', '2026-01-01', '2027-01-01');
  for (const asOf of ['2025-12-31', '2026-01-01', '2026-03-31', '2026-12-31', '2027-06-01', '2024-02-29']) f.add('earnedAsOf', { tx: policy, asOf }, () => ep.earnedAsOf(policy, asOf));
  f.add('earnedAsOf', { tx: tx(100, 'GEL', '2026-01-01', '2026-01-01', '2026-01-01'), asOf: '2026-01-01' }, () => ep.earnedAsOf(tx(100, 'GEL', '2026-01-01', '2026-01-01', '2026-01-01'), '2026-01-01'));
  f.add('earnedAsOf', { tx: tx(100, 'GEL', '2025-01-01', '2026-01-01', '2025-12-01'), asOf: '2026-01-01' }, () => ep.earnedAsOf(tx(100, 'GEL', '2025-01-01', '2026-01-01', '2025-12-01'), '2026-01-01'));
  for (let i = 0; i < 400; i++) {
    const rows = randomLedger(r);
    const t = pick(r, rows);
    const asOf = randomAsOf(r, t);
    f.add('earnedAsOf', { tx: t, asOf }, () => ep.earnedAsOf(t, asOf));
    const x = exactOf(t);
    if (chance(r, 0.3)) { x.minor = x.minor * 10_000_000_000n + BigInt(int(r, 0, 9_999_999)); if (x.cancels) x.cancels.minor = -x.minor; }
    f.add('earnedAsOfExact', { tx: exactShape(x), asOf }, () => ep.earnedAsOfExact(x, asOf));
  }
  const big = { minor: 9007199254740801n, bookedOn: '2026-01-01', coverFrom: '2026-01-01', coverTo: '2027-01-01' };
  for (const asOf of ['2026-12-31', '2026-06-30', '2026-01-01']) f.add('earnedAsOfExact', { tx: exactShape(big), asOf }, () => ep.earnedAsOfExact(big, asOf));
  for (let i = 0; i < 300; i++) {
    const t = randomPolicy(r);
    const span = dates.daysBetween(t.coverFrom, t.coverTo);
    const cancelOn = pick(r, [t.coverFrom, t.coverTo, dates.addDays(t.coverFrom, -3), dates.addDays(t.coverTo, -1), dates.addDays(t.coverTo, 2), dates.addDays(t.coverFrom, int(r, 0, span)), dates.addDays(t.coverFrom, int(r, 0, span))]);
    f.add('cancellationOf', { tx: t, cancelOn }, () => ep.cancellationOf(t, cancelOn));
  }
  {
    const c = ep.cancellationOf(policy, '2026-07-01');
    f.add('cancellationOf', { tx: c, cancelOn: '2026-08-01' }, () => ep.cancellationOf(c, '2026-08-01'));
    f.add('reversalOf', { tx: policy, bookedOn: '2026-08-01' }, () => ep.reversalOf(policy, '2026-08-01'));
    const u = ep.reversalOf(c, '2026-07-01');
    f.add('reversalOf', { tx: u, bookedOn: '2026-08-01' }, () => ep.reversalOf(u, '2026-08-01'));
  }
  for (let i = 0; i < 150; i++) {
    const t = randomPolicy(r);
    const c = ep.cancellationOf(t, dates.addDays(t.coverFrom, int(r, -5, dates.daysBetween(t.coverFrom, t.coverTo) - 1)));
    if (!c) continue;
    const bookedOn = dates.addDays(c.bookedOn, int(r, 0, 40));
    f.add('reversalOf', { tx: c, bookedOn }, () => ep.reversalOf(c, bookedOn));
  }
  for (let i = 0; i < 300; i++) {
    const txs = [...randomLedger(r), ...(chance(r, 0.4) ? randomLedger(r) : [])];
    const asOf = randomAsOf(r, pick(r, txs));
    f.add('premiumPosition', { txs, asOf }, () => ep.premiumPosition(txs, asOf));
    const from = dates.addDays(asOf, -int(r, 0, 120));
    f.add('earnedInPeriod', { txs, from, to: asOf }, () => ep.earnedInPeriod(txs, from, asOf));
  }
  for (let i = 0; i < 300; i++) {
    const rows = randomLedger(r);
    const t = pick(r, rows);
    f.add('inGel', { tx: t }, () => ep.inGel(t, rates));
  }
  // A cancellation whose own date is outside the cover it reverses: inGel refuses it.
  { const bad = { ...tx(-100, 'USD', '2026-01-01', '2027-02-01', '2027-03-01'), cancels: policy }; f.add('inGel', { tx: bad }, () => ep.inGel(bad, rates)); }
  for (let i = 0; i < 200; i++) {
    const txs = [...randomLedger(r), ...(chance(r, 0.4) ? randomLedger(r) : [])];
    const asOf = randomAsOf(r, pick(r, txs));
    f.add('premiumPositionGel', { txs, asOf }, () => ep.premiumPositionGel(txs, asOf, rates));
  }
  for (let i = 0; i < 100; i++) {
    const ledger = randomLedger(r);
    const ids = new Map<Any, string>(ledger.map((t, k) => [t, `row-${k}`]));
    const rowsOf = () => shuffle(mulberry32(1000 + i), ledger).map((t) => ({ id: ids.get(t)!, cancelsId: t.cancels ? ids.get(t.cancels)! : null, tx: { amount: t.amount, bookedOn: t.bookedOn, coverFrom: t.coverFrom, coverTo: t.coverTo } }));
    let rows = rowsOf();
    if (i % 10 === 9) rows = rows.filter((row) => row.id !== 'row-0'); // without the issue, a cancellation of it points at nothing
    const input = clone(rows);
    f.add('linkLedger', { rows: input }, () => ep.linkLedger(rows));
  }
  grandTotal += f.write();
}

// ======================================================================= commission, reserves, deductible
{
  const f = new File('finance-commission.json');
  const r = mulberry32(404);
  for (let i = 0; i < 300; i++) {
    const rows = randomLedger(r);
    const t = pick(r, rows);
    const percent = chance(r, 0.2) ? pick(r, [0, 17.5, 10, 100, 12.34, 33.33, 0.01, 99.99]) : int(r, 0, 10000) / 100;
    f.add('commissionTransaction', { tx: t, percent }, () => commission.commissionTransaction(t, percent));
  }
  const cp: [number, number, number][] = [[21000, 120000, 30000], [21000, 120000, 0], [21000, 120000, 150000], [21000, 0, 100], [21000, -5, 100], [21000, 120000, -100], [-21000, 120000, 60000], [1, 3, 1], [1, 2, 1], [-1, 2, 1]];
  for (let i = 0; i < 300; i++) { const premium = int(r, -1000, 10_000_000); cp.push([int(r, -100_000, 2_000_000), premium, int(r, -1000, premium + 100000)]); }
  for (const [commissionMinor, premiumMinor, paidMinor] of cp) f.add('commissionPayable', { commissionMinor, premiumMinor, paidMinor }, () => commission.commissionPayable(commissionMinor, premiumMinor, paidMinor));
  grandTotal += f.write();
}
{
  const f = new File('finance-reserves.json');
  const r = mulberry32(505);
  const pairs: [number, number][] = [[100000000, 20000000], [36500, 0], [10, 0], [30, 0], [25, 0], [75, 0], [101, 100], [100, 100], [100, 101], [0, 0], [9, 0], [50, 0], [-100, 0]];
  for (let i = 0; i < 300; i++) { const g = int(r, -1000, 10_000_000_000); pairs.push([g, chance(r, 0.3) ? 0 : int(r, 0, g + 1000)]); }
  for (const [grossWrittenGel, cededGel] of pairs) for (const line of ['other', 'health']) f.add('statutoryIbnr', { grossWrittenGel, cededGel, line }, () => reserves.statutoryIbnr(grossWrittenGel, cededGel, line));
  const big: [bigint, bigint][] = [[999999999999999989n, 0n], [999999999999999974n, 0n], [9007199254740810n, 9007199254740800n], [10n, 0n], [25n, 0n], [9n, 0n], [0n, 5n]];
  for (let i = 0; i < 200; i++) { const g = BigInt(int(r, 0, 999_999_999)) * 1_000_000_000n + BigInt(int(r, 0, 999_999_999)); big.push([g, chance(r, 0.5) ? 0n : g / BigInt(int(r, 1, 10))]); }
  for (const [g, c] of big) for (const line of ['other', 'health']) f.add('statutoryIbnrExact', { grossWrittenGel: g.toString(), cededGel: c.toString(), line }, () => reserves.statutoryIbnrExact(g, c, line));
  grandTotal += f.write();
}
{
  const f = new File('finance-deductible.json');
  const r = mulberry32(606);
  const series: [number, number[]][] = [[20000, [10000, 90000]], [20000, [100000]], [20000, [20000, 80000]], [20000, [5000, 5000, 90000]], [0, [10000, 90000]], [20000, [0, -500, 30000]]];
  for (let i = 0; i < 100; i++) series.push([pick(r, [0, 10000, 20000, 50000, int(r, 0, 300000)]), Array.from({ length: int(r, 1, 6) }, () => int(r, 0, 200000))]);
  for (const [agreed, payments] of series) {
    let withheld = 0;
    for (const gross of payments) {
      const already = withheld;
      f.add('deductibleToWithhold', { agreedMinor: agreed, alreadyWithheldMinor: already, grossMinor: gross }, () => deductible.deductibleToWithhold(agreed, already, gross));
      withheld += deductible.deductibleToWithhold(agreed, already, gross);
    }
  }
  for (let i = 0; i < 100; i++) {
    const agreed = int(r, -50000, 50000), already = int(r, -50000, 50000), gross = int(r, -50000, 50000);
    f.add('deductibleToWithhold', { agreedMinor: agreed, alreadyWithheldMinor: already, grossMinor: gross }, () => deductible.deductibleToWithhold(agreed, already, gross));
  }
  const lists: number[][] = [[], [20000, 20000], [15000], [20000, 5000], [0, 0, 0], [1, 1, 2]];
  for (let i = 0; i < 100; i++) { const v = pick(r, [0, 10000, 20000]); lists.push(Array.from({ length: int(r, 1, 4) }, () => (chance(r, 0.7) ? v : pick(r, [0, 10000, 20000])))); }
  for (const agreedMinor of lists) f.add('classDeductible', { agreedMinor }, () => deductible.classDeductible(agreedMinor));
  grandTotal += f.write();
}

// ======================================================================= receivable
function randomSchedule(r: Rnd, currency: string, start: string, total: number): Any[] {
  const p = pick(r, [1, 4, 12, 11, 2, 3]);
  const end = dates.addMonths(start, 12);
  let parts: { dueOn: string; minor: number }[];
  if (p === 11) parts = plan.planInstallments(11, start, end, total);
  else parts = plan.planInstallments(p as Any, start, end, total);
  let list = parts.map((x, k) => ({ id: `i${k + 1}`, dueOn: x.dueOn, amount: { minor: x.minor, currency } }));
  if (chance(r, 0.15) && list.length > 1) list[1] = { ...list[1]!, dueOn: list[0]!.dueOn }; // two due the same day
  if (chance(r, 0.3)) list = shuffle(r, list);
  return list;
}
function randomCredits(r: Rnd, currency: string, start: string, total: number): Any[] {
  const credits: Any[] = [];
  for (let k = int(r, 0, 4); k > 0; k--) {
    const kind = pick(r, ['payment', 'payment', 'payment', 'claim_offset', 'commission_offset']);
    const minor = chance(r, 0.1) ? -int(r, 1, 5000) : chance(r, 0.15) ? total : int(r, 1, Math.max(1, Math.floor(total / 2)));
    credits.push({ on: dates.addDays(start, int(r, -10, 400)), amount: { minor, currency }, kind });
  }
  return credits;
}
{
  const f = new File('finance-receivable.json');
  const r = mulberry32(707);
  for (let i = 0; i < 200; i++) {
    const gelMinor = chance(r, 0.1) ? pick(r, [0, 1, -100]) : int(r, 1, 50_000_000);
    const currency = pick(r, CURS);
    const paidOn = randDate(r, '2024-01-01', '2027-12-31');
    f.add('paymentCredit', { gelMinor, policyCurrency: currency, paidOn }, () => receivable.paymentCredit(gelMinor, currency, paidOn, rates));
  }
  for (let i = 0; i < 300; i++) {
    const currency = pick(r, CURS);
    const start = randDate(r, '2024-01-01', '2026-12-31');
    const total = int(r, 100, 5_000_000);
    const installments = chance(r, 0.05) ? [] : randomSchedule(r, currency, start, total);
    let credits = randomCredits(r, currency, start, total);
    if (i % 40 === 7) credits = [...credits, { on: start, amount: { minor: 100, currency: currency === 'GEL' ? 'USD' : 'GEL' }, kind: 'payment' }];
    const asOf = dates.addDays(start, int(r, -20, 420));
    f.add('receivableAsOf', { installments, credits, asOf }, () => receivable.receivableAsOf(installments, credits, asOf, rates));
    const graceDays = chance(r, 0.2) ? null : int(r, 0, 90);
    f.add('cancellableFrom', { installments, credits, asOf, graceDays }, () => receivable.cancellableFrom(receivable.receivableAsOf(installments, credits, asOf, rates), graceDays));
    if (installments.length > 0 && !(i % 40 === 7)) {
      let position: Any;
      try { position = receivable.receivableAsOf(installments, credits, asOf, rates); } catch { position = receivable.receivableAsOf(installments, credits, asOf, () => 2.5); }
      const gelMinor = chance(r, 0.05) ? pick(r, [0, -1]) : chance(r, 0.2) ? int(r, 5_000_000, 20_000_000) : int(r, 1, 3_000_000);
      const paidOn = dates.addDays(asOf, int(r, 0, 10));
      f.add('allocatePayment', { gelMinor, paidOn, currency, balances: position.balances }, () => receivable.allocatePayment(gelMinor, paidOn, currency, position.balances, rates));
    }
  }
  for (let i = 0; i < 200; i++) {
    const currency = pick(r, CURS);
    const claimAmount = { minor: chance(r, 0.1) ? -int(r, 1, 1000) : int(r, 0, 5_000_000), currency };
    const outstandingPremium = { minor: chance(r, 0.1) ? -int(r, 1, 1000) : int(r, 0, 5_000_000), currency: chance(r, 0.05) ? pick(r, CURS) : currency };
    const on = randDate(r, '2024-01-01', '2027-12-31');
    f.add('offsetClaim', { claimAmount, outstandingPremium, on }, () => receivable.offsetClaim(claimAmount, outstandingPremium, on));
  }
  // orderMirrorLines: the old test's histories, exactly as it drew them (its LCG and V8's sort), and more of our own.
  const mirror = (lines: (number | bigint)[], limit: number | bigint, hint?: number[], maxStates?: number) => ({ lines: lines.map(String), limit: String(limit), ...(hint ? { hint } : {}), ...(maxStates !== undefined ? { maxStates } : {}) });
  const fixed: [number[], number, number[]?, number?][] = [
    [[10000, -8000, 8000, -6000, 2000], 10000], [[2000, -6000, 8000, -8000, 10000], 10000], [[8000, 2000, 10000, -6000, -8000], 10000, [0, 1, 2, 3, 4]],
    [[36500, 4000, -10000, 6000], 36500, [0, 1, 2, 3]], [[10000, -10000, 10000], 10000], [[30000, -5000, 2000], 36500], [[500], 500], [[-5000, 5000], 10000],
    [[10000, 5000], 10000], [[], 100], [[0, 0], 0], [[1, 1, 1], 2], [[3, -1, -1, -1], 3], [[5, -5, 5, -5, 5], 5, [4, 3, 2, 1, 0]],
  ];
  const h61 = [10000, -6000, 2000, -8000, 8000, ...Array.from({ length: 7 }, () => [-100, 100]).flat()];
  fixed.push([h61, 10000], [h61, 10000, undefined, 3], [h61, 10000, undefined, 1], [h61, 10000, undefined, 0], [[10000, -8000, 8000, -6000, 2000, ...Array.from({ length: 7 }, () => [-100, 100]).flat()], 10000]);
  for (const [lines, limit, hint, maxStates] of fixed) f.add('orderMirrorLines', mirror(lines, limit, hint, maxStates), () => receivable.orderMirrorLines(lines, limit, hint, maxStates));
  { const huge = 9007199254740993n; const lines = [huge, 1n, -1n]; f.add('orderMirrorLines', mirror(lines, huge), () => receivable.orderMirrorLines(lines, huge)); }
  {
    let seed = 7;
    const random = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
    for (let k = 0; k < 300; k++) {
      const limit = 1000 + Math.floor(random() * 9000);
      const lines: number[] = [];
      let sum = 0;
      for (let step = 0; step < 1 + Math.floor(random() * 12); step++) {
        const release = sum > 0 && (random() < 0.4 || sum === limit);
        const amount = release ? -(1 + Math.floor(random() * sum)) : 1 + Math.floor(random() * (limit - sum));
        lines.push(amount);
        sum += amount;
      }
      const shuffled = lines.map((v, i) => ({ v, i })).sort(() => random() - 0.5).map((x) => x.v);
      f.add('orderMirrorLines.testRandom', mirror(shuffled, limit), () => receivable.orderMirrorLines(shuffled, limit));
    }
    for (let k = 0; k < 500; k++) {
      const limit = 10000 + Math.floor(random() * 90000);
      const lines: number[] = [];
      const seen: number[] = [];
      let sum = 0;
      for (let step = 0; step < 1 + Math.floor(random() * 30); step++) {
        const release = sum > 0 && (random() < 0.4 || sum === limit);
        let amount = release ? -(1 + Math.floor(random() * sum)) : 1 + Math.floor(random() * (limit - sum));
        const again = seen.find((p) => random() < 0.8 && (release ? p < 0 && -p <= sum : p > 0 && p <= limit - sum));
        if (again !== undefined) amount = again;
        seen.push(amount);
        lines.push(amount);
        sum += amount;
      }
      const input = lines.map((v) => ({ v, r: random() })).sort((a, b) => a.r - b.r).map((x) => x.v);
      f.add('orderMirrorLines.testPractical', mirror(input, limit), () => receivable.orderMirrorLines(input, limit));
    }
  }
  for (let k = 0; k < 200; k++) {
    const limit = int(r, 1, 50000);
    const lines: number[] = [];
    let sum = 0;
    for (let step = int(r, 1, 15); step > 0; step--) {
      const release = sum > 0 && (chance(r, 0.4) || sum === limit);
      const amount = release ? -int(r, 1, sum) : int(r, 1, limit - sum);
      lines.push(amount);
      sum += amount;
    }
    if (chance(r, 0.15)) lines.push(int(r, limit - sum + 1, limit - sum + 100)); // not a valid history
    const input = shuffle(r, lines);
    const hint = chance(r, 0.3) ? shuffle(r, input.map((_, i) => i)) : undefined;
    const maxStates = chance(r, 0.1) ? int(r, 1, 20) : undefined;
    f.add('orderMirrorLines', mirror(input, limit, hint, maxStates), () => receivable.orderMirrorLines(input, limit, hint, maxStates));
  }
  // reduceInstallments and rescheduleInstallments
  const four = ['2026-04-01', '2026-07-01', '2026-10-01', '2027-01-01'].map((dueOn, n) => ({ id: String(n + 1), dueOn, amount: { minor: 9125, currency: 'GEL' } }));
  const reductions: [Any[], Record<string, number>, number, string][] = [
    [four, { 1: 9125 }, 10000, 'last_first'], [four, { 1: 9125 }, 10000, 'equal_unpaid'], [four, { 1: 9125, 2: 9125, 3: 9125 }, 10000, 'equal_unpaid'],
    [four, { 1: 9125 }, 0, 'equal_unpaid'], [four, { 1: 9125 }, 36501, 'equal_unpaid'], [four, { 1: 9125 }, 36500, 'equal_unpaid'], [four, {}, -5, 'last_first'],
    [[{ id: 'a', dueOn: '2026-04-01', amount: { minor: 1000, currency: 'GEL' } }, { id: 'b', dueOn: '2026-07-01', amount: { minor: 10000, currency: 'GEL' } }, { id: 'c', dueOn: '2026-10-01', amount: { minor: 10000, currency: 'GEL' } }], {}, 9000, 'equal_unpaid'],
  ];
  for (let i = 0; i < 300; i++) {
    const currency = pick(r, CURS);
    const start = randDate(r, '2024-01-01', '2026-12-31');
    const total = int(r, 1, 2_000_000);
    const installments = randomSchedule(r, currency, start, total);
    const settled: Record<string, number> = {};
    for (const inst of installments) if (chance(r, 0.4)) settled[inst.id] = chance(r, 0.5) ? inst.amount.minor : int(r, 0, inst.amount.minor + 100);
    const reductionMinor = chance(r, 0.1) ? total + int(r, 1, 100) : chance(r, 0.1) ? total : int(r, 0, total);
    reductions.push([installments, settled, reductionMinor, pick(r, ['last_first', 'equal_unpaid'])]);
  }
  for (const [installments, settled, reductionMinor, order] of reductions) {
    f.add('reduceInstallments', { installments, settled, reductionMinor, order }, () => Object.fromEntries(receivable.reduceInstallments(installments, new Map(Object.entries(settled)), reductionMinor, order)));
  }
  for (let i = 0; i < 200; i++) {
    const currency = pick(r, CURS);
    const start = randDate(r, '2024-01-01', '2026-12-31');
    const total = int(r, 1, 2_000_000);
    const installments = randomSchedule(r, currency, start, total);
    const targetMinor = chance(r, 0.1) ? -int(r, 1, 100) : int(r, 0, total + 1000);
    f.add('rescheduleInstallments', { installments, targetMinor }, () => receivable.rescheduleInstallments(installments, targetMinor));
  }
  grandTotal += f.write();
}

// ======================================================================= total loss
{
  const f = new File('finance-total-loss.json');
  const r = mulberry32(808);
  for (let i = 0; i < 300; i++) {
    const currency = pick(r, CURS);
    const ledger = randomLedger(r, currency).filter((t) => !t.cancels || chance(r, 0.1));
    const issue = ledger[0];
    const total = ledger.reduce((s: number, t: Any) => s + t.amount.minor, 0);
    const installments = randomSchedule(r, currency, issue.coverFrom, Math.max(total, 1));
    const credits = randomCredits(r, currency, issue.coverFrom, Math.max(total, 1));
    const lossOn = dates.addDays(issue.coverFrom, int(r, -10, dates.daysBetween(issue.coverFrom, issue.coverTo) + 10));
    let position: Any;
    try { position = receivable.receivableAsOf(installments, credits, lossOn, rates); } catch { continue; }
    const claimAmount = { minor: chance(r, 0.2) ? int(r, 0, 5000) : int(r, 100_000, 50_000_000), currency: chance(r, 0.03) ? pick(r, CURS) : currency };
    for (const deduction of ['none', 'earned_unpaid', 'all_unpaid']) {
      f.add('settleTotalLoss', { premium: ledger, receivable: position, claimAmount, lossOn, deduction }, () => totalLoss.settleTotalLoss({ premium: ledger, receivable: position, claimAmount, lossOn, deduction }));
    }
  }
  grandTotal += f.write();
}

// ======================================================================= plan, cancel preview
{
  const f = new File('finance-plan.json');
  const r = mulberry32(909);
  for (let v = -1; v <= 14; v++) f.add('isPaymentPlan', { value: v }, () => plan.isPaymentPlan(v));
  for (let i = 0; i < 200; i++) {
    const from = randDate(r, '2023-01-01', '2028-12-31');
    const to = chance(r, 0.5) ? dates.addMonths(from, 12) : dates.addDays(from, int(r, 300, 400));
    const p = pick(r, [1, 4, 11, 12]);
    f.add('fitsPlan', { plan: p, from, to }, () => plan.fitsPlan(p, from, to));
  }
  const fixed: [number, string, string, number][] = [[1, '2026-10-01', '2027-10-01', 40000], [4, '2026-10-01', '2027-10-01', 40000], [12, '2026-01-01', '2027-01-01', 100000], [4, '2026-01-01', '2026-04-11', 40000], [11, '2026-01-01', '2027-01-01', 120000], [11, '2026-10-01', '2027-10-01', 100000], [11, '2026-01-01', '2026-07-01', 60000], [4, '2026-01-31', '2027-01-31', 100001], [12, '2024-01-31', '2025-01-31', 7], [11, '2024-02-29', '2025-02-28', 5], [4, '2026-04-11', '2026-01-01', 40000], [0, '2026-01-01', '2027-01-01', 100], [11, '2026-01-31', '2027-01-31', -100]];
  for (let i = 0; i < 400; i++) {
    const p = pick(r, [1, 4, 11, 12, 1, 4, 11, 12, 2, 3, 6]);
    const from = randDate(r, '2023-01-01', '2028-12-31');
    const to = chance(r, 0.6) ? dates.addMonths(from, pick(r, [12, 12, 6, 3, 24, 1, 18])) : dates.addDays(from, int(r, 1, 800));
    const totalMinor = chance(r, 0.1) ? int(r, -10000, 20) : int(r, 1, 10_000_000);
    fixed.push([p, from, to, totalMinor]);
  }
  for (const [p, from, to, totalMinor] of fixed) f.add('planInstallments', { plan: p, from, to, totalMinor }, () => plan.planInstallments(p, from, to, totalMinor));
  grandTotal += f.write();
}
{
  const f = new File('finance-cancel-preview.json');
  const r = mulberry32(1010);
  const year = tx(36500, 'GEL', '2026-01-01', '2026-01-01', '2027-01-01');
  const cover = { from: '2026-01-01', to: '2027-01-01' };
  for (const [on, paid] of [['2026-07-01', 9125], ['2026-10-05', 9125], ['2026-07-01', 36500], ['2025-12-01', 0], ['2027-03-01', 0], ['2026-01-01', 0], ['2026-12-31', 0], ['2027-01-01', 0]] as const) {
    f.add('cancellationPreview', { rows: [year], on, paidMinor: paid, cover }, () => cancelPreview.cancellationPreview([year], on, paid, cover));
  }
  for (let i = 0; i < 300; i++) {
    const currency = pick(r, CURS);
    const rows = randomLedger(r, currency).filter((t) => !t.cancels);
    const issue = rows[0];
    const c = { from: issue.coverFrom, to: issue.coverTo };
    const on = dates.addDays(issue.coverFrom, int(r, -30, dates.daysBetween(issue.coverFrom, issue.coverTo) + 30));
    const paidMinor = chance(r, 0.2) ? 0 : int(r, 0, Math.abs(issue.amount.minor) + 1000);
    f.add('cancellationPreview', { rows, on, paidMinor, cover: c }, () => cancelPreview.cancellationPreview(rows, on, paidMinor, c));
  }
  // A cancellation among the rows is refused by cancellationOf.
  { const c = ep.cancellationOf(year, '2026-06-01'); f.add('cancellationPreview', { rows: [year, c], on: '2026-07-01', paidMinor: 0, cover }, () => cancelPreview.cancellationPreview([year, c], '2026-07-01', 0, cover)); }
  grandTotal += f.write();
}

// ======================================================================= impairment
{
  const f = new File('finance-impairment.json');
  const r = mulberry32(1111);
  for (let d = '2023-01-01'; d <= '2025-12-31'; d = dates.addDays(d, 1)) f.add('bandCutoffs', { asOf: d }, () => impairment.bandCutoffs(d));
  for (const d of ['1900-03-31', '2100-02-28', '2000-02-29', '1999-11-30']) f.add('bandCutoffs', { asOf: d }, () => impairment.bandCutoffs(d));
  const year2023 = tx(365000, 'GEL', '2023-01-01', '2023-01-01', '2024-01-01');
  const sets: [Any[], number, string][] = [[[year2023], 0, '2023-12-31'], [[year2023], 100000, '2023-12-31'], [[year2023], 365000, '2023-12-31'], [[year2023], 400000, '2023-12-31'],
    [[tx(365000, 'USD', '2022-01-01', '2022-01-01', '2023-01-01')], 0, '2023-12-31'], [[year2023, ep.cancellationOf(year2023, '2023-04-01')], 0, '2023-12-31'], [[{ ...year2023, bookedOn: '2024-02-01' }], 0, '2023-12-31'], [[{ ...year2023, bookedOn: '2023-03-15' }], 0, '2023-12-31']];
  for (let i = 0; i < 300; i++) {
    const ledger = randomLedger(r, undefined, '2022-01-01', '2026-06-30');
    const issue = ledger[0];
    const asOf = chance(r, 0.6)
      ? dates.addDays(dates.addMonths(`${issue.coverFrom.slice(0, 7)}-01`, int(r, 0, 30)), -1) // a month end
      : dates.addDays(issue.coverFrom, int(r, -30, 900));
    const total = ledger.reduce((s: number, t: Any) => s + t.amount.minor, 0);
    const paid = chance(r, 0.25) ? 0 : chance(r, 0.15) ? total + int(r, 0, 1000) : chance(r, 0.05) ? -int(r, 1, 1000) : int(r, 0, Math.max(0, total));
    sets.push([ledger, paid, asOf]);
  }
  sets.forEach(([txs, paidMinor, asOf], k) => {
    f.add('earnedByAge', { txs, asOf }, () => impairment.earnedByAge(txs, asOf));
    const percent = k % 7 === 3 ? { over12: 100, m9to12: 75, m6to9: 40, m3to6: 15, m0to3: 5 } : undefined;
    f.add('impairmentOf', { txs, paidMinor, asOf, ...(percent ? { percent } : {}) }, () => impairment.impairmentOf(txs, paidMinor, asOf, percent));
    f.add('withoutReserve', { txs, paidMinor, asOf }, () => impairment.withoutReserve(impairment.impairmentOf(txs, paidMinor, asOf)));
    const exact = txs.map(exactOf);
    if (k % 5 === 4) for (const x of exact) { x.minor *= 1_000_000_000n; if (x.cancels) x.cancels.minor *= 1_000_000_000n; if (x.cancels?.cancels) x.cancels.cancels.minor *= 1_000_000_000n; }
    const paidExact = k % 5 === 4 ? BigInt(paidMinor) * 1_000_000_000n + 7n : BigInt(paidMinor);
    f.add('earnedByAgeExact', { txs: exact.map(exactShape), asOf }, () => impairment.earnedByAgeExact(exact, asOf));
    f.add('impairmentOfExact', { txs: exact.map(exactShape), paid: paidExact.toString(), asOf, ...(percent ? { percent } : {}) }, () => impairment.impairmentOfExact(exact, paidExact, asOf, percent));
  });
  { const big = { minor: 9007199254740801n, bookedOn: '2026-01-01', coverFrom: '2026-01-01', coverTo: '2027-01-01' }; f.add('impairmentOfExact', { txs: [exactShape(big)], paid: '0', asOf: '2026-12-31' }, () => impairment.impairmentOfExact([big], 0n, '2026-12-31')); }
  grandTotal += f.write();
}

// ======================================================================= payment authorisation
{
  const f = new File('finance-payment-authorization.json');
  const r = mulberry32(1212);
  const THRESHOLD = 1_000_000;
  const table: Record<string, number> = { 'USD 2026-10-15': 2.7, 'USD 2026-10-16': 2.8, 'EUR 2026-10-15': 3.1234, 'EUR 2026-10-16': 2.000001 };
  const fixedRates = (c: string, d: string) => table[`${c} ${d}`] ?? 0;
  const cases: [number, string, string, number, boolean][] = [[999_999, 'GEL', '2026-10-15', THRESHOLD, true], [1_000_000, 'GEL', '2026-10-15', THRESHOLD, true], [1_000_001, 'GEL', '2026-10-15', THRESHOLD, true],
    [370_370, 'USD', '2026-10-15', THRESHOLD, true], [370_370, 'USD', '2026-10-16', THRESHOLD, true], [370_371, 'USD', '2026-10-15', THRESHOLD, true], [320_164, 'EUR', '2026-10-15', THRESHOLD, true],
    [320_165, 'EUR', '2026-10-15', THRESHOLD, true], [500_000, 'EUR', '2026-10-16', THRESHOLD, true], [100, 'USD', '2026-10-17', THRESHOLD, true], [0, 'GEL', '2026-10-15', THRESHOLD, true],
    [-1_000_001, 'GEL', '2026-10-15', THRESHOLD, true], [1, 'GEL', '15.10.2026', THRESHOLD, true], [1, 'GEL', '2026-10-15', -1, true], [1, 'GEL', '2026-10-15', 0, true], [1, 'GEL', '2026-02-30', 0, true], [0, 'GEL', '15.10.2026', -1, true]];
  for (let i = 0; i < 300; i++) {
    const minor = chance(r, 0.05) ? pick(r, [0, -5]) : chance(r, 0.5) ? int(r, 1, 2_000_000) : int(r, 300_000, 500_000);
    const day = chance(r, 0.05) ? pick(r, ['2026-13-01', '', '2026-1-1']) : randDate(r, '2024-01-01', '2027-12-31');
    cases.push([minor, pick(r, CURS), day, chance(r, 0.05) ? -1 : pick(r, [THRESHOLD, THRESHOLD, 0, int(r, 0, 2_000_000)]), false]);
  }
  for (const [minor, currency, paidOn, threshold, useFixed] of cases) {
    const calls: [string, string][] = [];
    const provider = (c: string, d: string) => { calls.push([c, d]); return useFixed ? fixedRates(c, d) : rates(c, d); };
    const result = run(() => paymentAuth.paymentAuthorization({ minor, currency }, paidOn, provider, threshold));
    (f.cases.paymentAuthorization ??= []).push({ in: { payment: { minor, currency }, paidOn, thresholdGelMinor: threshold, rates: useFixed ? 'test' : 'table' }, ...result, calls });
  }
  grandTotal += f.write();
}

// ======================================================================= reinsurance
{
  const f = new File('finance-reinsurance.json');
  const r = mulberry32(1313);
  // Percents with four decimals (numeric(7, 4)), or more decimals but never an exact half at the fifth: there a JS double
  // and the exact decimal round the same way. (An exact half such as 35.85265 rounds down through doubles — see the report.)
  const fourDec = () => int(r, 0, 1_000_000) / 10000;
  const manyDec = () => { let v: number; do { v = int(r, 0, 100_000_000) / 1_000_000; } while (Math.round(v * 1e6) % 100 === 50); return v; };
  const percents = [0, 30, 33.3333, 66.6667, 0.0001, 100, 7.5, 12.5, 50, 99.9999, 22.5, 17.5];
  for (let i = 0; i < 200; i++) percents.push(chance(r, 0.7) ? fourDec() : manyDec());
  for (const percent of percents) f.add('percentToUnits', { percent }, () => reinsurance.percentToUnits(percent));
  const termsList: Any[] = [{ kind: 'quota_share', cededPercent: 30, commissionPercent: 20 }, { kind: 'surplus', retention: 10_000_000, lines: 4, commissionPercent: 0 }, { kind: 'excess_of_loss', priority: 1, limit: 2, commissionPercent: 0 }, { kind: 'facultative', commissionPercent: 0 }, { kind: 'surplus', commissionPercent: 0 }, { kind: 'surplus', retention: 0, lines: 3, commissionPercent: 0 }];
  for (let i = 0; i < 200; i++) {
    const kind = pick(r, ['quota_share', 'facultative', 'surplus', 'surplus', 'excess_of_loss']);
    if (kind === 'surplus') termsList.push({ kind, retention: chance(r, 0.1) ? pick(r, [0, -100]) : int(r, 1, 50_000_000), lines: int(r, 0, 9), commissionPercent: 0 });
    else if (kind === 'excess_of_loss') termsList.push({ kind, priority: int(r, 0, 1000000), limit: int(r, 0, 1000000), commissionPercent: 0 });
    else termsList.push({ kind, cededPercent: chance(r, 0.1) ? pick(r, [-5, 120, 100.00004, 0.00006, 12.34564, 12.34566]) : chance(r, 0.7) ? fourDec() : manyDec(), commissionPercent: fourDec() });
  }
  for (const terms of termsList) {
    for (const sumInsuredMinor of [null, 0, 30_000_000, 8_000_000, 100_000_000, int(r, 1, 500_000_000)]) f.add('cededPercentOf', { terms, sumInsuredMinor }, () => reinsurance.cededPercentOf(terms, sumInsuredMinor));
  }
  for (let i = 0; i < 300; i++) {
    const minor = chance(r, 0.1) ? int(r, -10, 10) : int(r, -10_000_000, 100_000_000);
    const sharePercent = pick(r, percents);
    f.add('cededAmount', { minor, sharePercent }, () => reinsurance.cededAmount(minor, sharePercent));
    const alreadyPercent = pick(r, [0, 50, 33.3333, 66.6666, 12.5, fourDec(), manyDec()]);
    f.add('cededSlice', { minor, alreadyPercent, sharePercent }, () => reinsurance.cededSlice(minor, alreadyPercent, sharePercent));
  }
  for (const text of ['100.0000', '50.0000', '33.3333', '0.0001', '66.6667', '30', '7.5', '12.34567', '', '.5', '5.', '-1', '1e2', ' 5', '5 ', '0', '00012.3400', '12.34\n', '１2']) f.add('percentUnitsExact', { text }, () => reinsurance.percentUnitsExact(text));
  for (let i = 0; i < 300; i++) {
    const minor = chance(r, 0.3) ? BigInt(int(r, 0, 999_999_999)) * 1_000_000_000n + BigInt(int(r, 0, 999_999_999)) : BigInt(int(r, -100_000, 100_000_000));
    const already = BigInt(int(r, 0, 1_000_000));
    const share = BigInt(int(r, 0, 1_000_000));
    f.add('cededSliceExact', { minor: minor.toString(), alreadyUnits: already.toString(), shareUnits: share.toString() }, () => reinsurance.cededSliceExact(minor, already, share));
  }
  const claimTerms: Any[] = [{ kind: 'quota_share' }, { kind: 'excess_of_loss', priority: 100_000, limit: 500_000 }, { kind: 'excess_of_loss', priority: 10_000, limit: 0 }, { kind: 'excess_of_loss' }, { kind: 'surplus' }, { kind: 'facultative' }, { kind: 'excess_of_loss', priority: 10_000, limit: -5 }];
  for (let i = 0; i < 300; i++) {
    const terms = chance(r, 0.5) ? pick(r, claimTerms) : { kind: 'excess_of_loss', priority: int(r, 0, 500_000), ...(chance(r, 0.8) ? { limit: int(r, 0, 1_000_000) } : {}) };
    const sharePercent = pick(r, percents);
    const amountMinor = chance(r, 0.1) ? int(r, -1000, 0) : int(r, 0, 2_000_000);
    f.add('reinsurerShareOfClaim', { terms, sharePercent, amountMinor }, () => reinsurance.reinsurerShareOfClaim(terms, sharePercent, amountMinor));
    const exactTerms = { kind: terms.kind, ...(terms.priority !== undefined ? { priority: BigInt(terms.priority) } : {}), ...(terms.limit !== undefined ? { limit: BigInt(terms.limit) } : {}) };
    const units = BigInt(reinsurance.percentToUnits(sharePercent));
    const amount = chance(r, 0.2) ? BigInt(amountMinor) * 1_000_000_000n : BigInt(amountMinor);
    f.add('reinsurerShareOfClaimExact', { terms: { kind: terms.kind, ...(terms.priority !== undefined ? { priority: String(terms.priority) } : {}), ...(terms.limit !== undefined ? { limit: String(terms.limit) } : {}) }, shareUnits: units.toString(), amountMinor: amount.toString() }, () => reinsurance.reinsurerShareOfClaimExact(exactTerms, units, amount));
  }
  const planCases: [Any[], string][] = [];
  const share = (kind: string, sharePercent: number, currency: string) => ({ kind, sharePercent, currency });
  planCases.push([[share('quota_share', 60, 'GEL'), share('quota_share', 60, 'GEL')], 'GEL'], [[share('quota_share', 60, 'GEL'), share('quota_share', 40, 'GEL')], 'GEL'], [[share('quota_share', 70, 'GEL'), share('facultative', 40, 'GEL')], 'GEL'],
    [[share('excess_of_loss', 0, 'GEL')], 'USD'], [[share('excess_of_loss', 0, 'USD')], 'USD'], [[share('quota_share', 30, 'GEL'), share('excess_of_loss', 0, 'GEL')], 'GEL'], [[share('excess_of_loss', 0, 'GEL'), share('excess_of_loss', 0, 'GEL')], 'GEL'], [[], 'GEL'],
    [[share('quota_share', 0.00004, 'GEL'), share('excess_of_loss', 0, 'GEL')], 'GEL'], [[share('quota_share', 100.00004, 'GEL')], 'GEL'], [[share('quota_share', 100.00005, 'GEL')], 'GEL']);
  for (let i = 0; i < 300; i++) {
    const shares = Array.from({ length: int(r, 0, 4) }, () => share(pick(r, ['quota_share', 'surplus', 'facultative', 'excess_of_loss']), chance(r, 0.2) ? 0 : fourDec(), chance(r, 0.85) ? 'GEL' : pick(r, ['USD', 'EUR'])));
    planCases.push([shares, chance(r, 0.85) ? 'GEL' : pick(r, ['USD', 'EUR'])]);
  }
  for (const [shares, riskCurrency] of planCases) f.add('checkCessionPlan', { shares, riskCurrency }, () => reinsurance.checkCessionPlan(shares, riskCurrency));
  for (let i = 0; i < 300; i++) {
    const rows = randomLedger(r);
    const t = pick(r, rows);
    const sharePercent = pick(r, percents);
    f.add('cessionOf', { tx: t, sharePercent }, () => reinsurance.cessionOf(t, sharePercent));
    let cession: Any;
    try { cession = reinsurance.cessionOf(t, sharePercent); } catch { continue; }
    const commissionPercent = pick(r, [0, 20, 22.5, fourDec(), manyDec()]);
    f.add('commissionOf', { cession, commissionPercent }, () => reinsurance.commissionOf(cession, commissionPercent));
  }
  const tp: [number, string, string, string, string][] = [[36_500_000, '2026-01-01', '2026-12-31', '2026-01-01', '2026-01-31'], [36_500_000, '2026-01-01', '2026-12-31', '2026-02-01', '2026-02-28'], [36_500_000, '2026-01-01', '2026-12-31', '2025-12-01', '2027-01-31'], [100, '2026-01-01', '2026-01-01', '2026-01-01', '2026-01-01'], [100, '2026-01-01', '2025-12-30', '2026-01-01', '2026-01-02']];
  for (let i = 0; i < 300; i++) {
    const treatyFrom = randDate(r, '2023-01-01', '2027-12-31');
    const treatyTo = dates.addDays(chance(r, 0.6) ? dates.addMonths(treatyFrom, 12) : dates.addDays(treatyFrom, int(r, 1, 700)), -1);
    const from = dates.addDays(treatyFrom, int(r, -60, 400));
    const to = dates.addDays(from, int(r, -5, 120));
    tp.push([int(r, -1_000_000, 100_000_000), treatyFrom, treatyTo, from, to]);
  }
  for (const [premiumMinor, treatyFrom, treatyTo, from, to] of tp) f.add('treatyPremiumInPeriod', { premiumMinor, treatyFrom, treatyTo, from, to }, () => reinsurance.treatyPremiumInPeriod(premiumMinor, treatyFrom, treatyTo, from, to));
  grandTotal += f.write();
}

// ======================================================================= subrogation
const KINDS = ['claimed', 'confirmed', 'received', 'cancelled'] as const;
function randomPayments(r: Rnd, from: string): Any[] {
  return Array.from({ length: int(r, 0, 3) }, () => {
    const gross = BigInt(int(r, 1, 500_000));
    const withheld = BigInt(int(r, 0, 20_000));
    const offset = chance(r, 0.3) ? BigInt(int(r, 0, 30_000)) : 0n;
    return { amount: gross, deductibleWithheld: withheld, premiumOffset: offset, netPaidGel: gross - withheld - offset, offsetGel: offset, paidOn: dates.addDays(from, int(r, -30, 40)) };
  });
}
const others = (r: Rnd, from: string): Any[] => Array.from({ length: chance(r, 0.6) ? 0 : int(r, 1, 3) }, () => ({ amount: chance(r, 0.2) ? -BigInt(int(r, 1, 10_000)) : BigInt(int(r, 1, 100_000)), receivedOn: dates.addDays(from, int(r, -40, 40)) }));
const ser = (v: unknown) => JSON.parse(json(v));
/** A case history grown by proposals that the old checkMovement accepted. Every proposal is a case of its own. */
function growCase(r: Rnd, f: File | null, start: string, claim: Any, steps: number, prefix: string): Any[] {
  const ms: Any[] = [];
  for (let s = 0; s < steps; s++) {
    let next: Any;
    const bookedOn = dates.addDays(start, int(r, 0, 60));
    if (ms.length > 0 && chance(r, 0.25)) {
      const target = chance(r, 0.1) ? { id: 'nope' } : pick(r, ms);
      next = { kind: 'reversal', reversesId: target.id, bookedOn: chance(r, 0.15) ? dates.addDays(target.bookedOn ?? start, -int(r, 1, 5)) : bookedOn };
    } else {
      const kind = pick(r, KINDS);
      const amount = chance(r, 0.05) ? pick(r, [0n, -1n]) : BigInt(int(r, 1, 300_000));
      const court = kind === 'cancelled' ? pick(r, [{ number: 'სასამართლო 2/123-26', date: '2026-10-20' }, { number: 'სასამართლო 2/123-26', date: '2026-10-20' }, { number: ' ', date: '2026-10-20' }, null, undefined, { number: String.fromCharCode(0xa0, 0xfeff), date: '2026-10-20' }, { number: String.fromCharCode(0x85), date: '2026-10-20' }]) : undefined;
      next = { kind, amount, bookedOn, ...(court !== undefined ? { court } : {}) };
    }
    const err = subrogation.checkMovement(ms, next, claim);
    f?.add('checkMovement', ser({ movements: ms, next, claim }), () => err);
    if (err === null) ms.push(subrogation.asMovement(`${prefix}${s}`, next, ms));
  }
  return ms;
}
{
  const f = new File('finance-subrogation.json');
  const r = mulberry32(1414);
  const texts = ['1000', '0.1', '1234.56', '9999999999999999.99', '', '-1', '1.234', '1,5', '1e3', 'abc', '.5', '5.', ' 12.5 ', '\t12.50\n', '00012', '12345678901234567', '1234567890123456', String.fromCharCode(0xa0) + '7.1', String.fromCharCode(0xfeff) + '7', String.fromCharCode(0x85) + '7', String.fromCharCode(0x2028) + '7', '７', '1.2.3', '+5'];
  for (let i = 0; i < 150; i++) texts.push(`${int(r, 0, 99999999)}${chance(r, 0.6) ? '.' + String(int(r, 0, 999)).slice(0, int(r, 1, 3)) : ''}`);
  for (const text of texts) f.add('toTetri', { text }, () => subrogation.toTetri(text));
  const tetris = [0n, 5n, -5n, 123456n, -60000n, 100n, 999999999999999999n, -1n];
  for (let i = 0; i < 150; i++) tetris.push(BigInt(int(r, -10_000_000, 10_000_000)) * (chance(r, 0.2) ? 1_000_000_000n : 1n));
  for (const t of tetris) f.add('fromTetri', { tetri: t.toString() }, () => subrogation.fromTetri(t));
  for (let i = 0; i < 120; i++) {
    const start = randDate(r, '2026-01-01', '2026-12-31');
    const claim = { payments: randomPayments(r, start), others: others(r, start) };
    if (chance(r, 0.3)) claim.payments.push({ amount: 10_000_000n, deductibleWithheld: 0n, premiumOffset: 0n, netPaidGel: 10_000_000n, offsetGel: 0n, paidOn: dates.addDays(start, -100) });
    const ms = growCase(r, f, start, claim, int(r, 3, 14), `c${i}-`);
    for (const asOf of [undefined, start, dates.addDays(start, int(r, 0, 70)), dates.addDays(start, -1)]) f.add('totals', ser({ movements: ms, ...(asOf ? { asOf } : {}) }), () => subrogation.totals(ms, asOf));
    const day = dates.addDays(start, int(r, -30, 70));
    f.add('reimbursed', ser({ payments: claim.payments, asOf: day }), () => subrogation.reimbursed(claim.payments, day));
    f.add('room', ser({ claim, asOf: day }), () => subrogation.room(claim, day));
    const t = subrogation.totals(ms);
    for (const current of ['opened', 'evidence_confirmed', 'demand_sent', 'accepted', 'disputed', 'rejected', 'partially_received', 'closed']) f.add('statusAfterMoney', ser({ current, totals: t }), () => subrogation.statusAfterMoney(current, t));
    const next = ms.length > 0 && chance(r, 0.5) ? { kind: 'reversal', reversesId: pick(r, [...ms.map((m: Any) => m.id), 'nope']), bookedOn: day } : { kind: pick(r, KINDS), amount: BigInt(int(r, 1, 1000)), bookedOn: day };
    f.add('asMovement', ser({ id: 'new-1', next, movements: ms }), () => subrogation.asMovement('new-1', next, ms));
  }
  // Errors of totals: a reversal of a reversal, of nothing.
  {
    const ms = [{ id: 'k', kind: 'confirmed', amount: 100n, bookedOn: '2026-10-01', reversesId: null }, { id: 'x', kind: 'reversal', amount: -100n, bookedOn: '2026-10-02', reversesId: 'k' }, { id: 'y', kind: 'reversal', amount: 100n, bookedOn: '2026-10-03', reversesId: 'x' }];
    f.add('totals', ser({ movements: ms }), () => subrogation.totals(ms));
    f.add('totals', ser({ movements: ms, asOf: '2026-10-02' }), () => subrogation.totals(ms, '2026-10-02'));
    const orphan = [{ id: 'z', kind: 'reversal', amount: -1n, bookedOn: '2026-10-02', reversesId: null }];
    f.add('totals', ser({ movements: orphan }), () => subrogation.totals(orphan));
  }
  const facts: Any[] = [{ classCode: '5.1', coverageCode: 'damage' }, { classCode: '5.1', coverageCode: 'equipment' }, { classCode: '14', coverageCode: 'assistance' }, { classCode: '3.2', coverageCode: 'accident' }, { classCode: '5.1', coverageCode: 'theft' }, { classCode: '5.1', coverageCode: null }, { classCode: '5.10', coverageCode: 'damage' }, { classCode: ' 5.1', coverageCode: 'damage' }, { classCode: '14', coverageCode: null }, { classCode: '5.1', coverageCode: 'Theft' }];
  for (const claim of facts) f.add('ineligibility', { claim }, () => subrogation.ineligibility(claim));
  for (let i = 0; i < 200; i++) {
    const confirmed = BigInt(int(r, 0, 2_000_000));
    const insurer = chance(r, 0.2) ? null : { limit: chance(r, 0.2) ? null : BigInt(int(r, 0, 2_000_000)), response: pick(r, ['accepted', 'disputed', 'rejected', null]) };
    f.add('expectedShares', ser({ confirmed, insurer }), () => subrogation.expectedShares(confirmed, insurer));
  }
  grandTotal += f.write();
}

// ======================================================================= subrogation report
{
  const f = new File('finance-subrogation-report.json');
  const r = mulberry32(1515);
  const stamps = ['2026-10-31T21:30:00Z', '2026-10-31T19:59:59Z', '2026-10-31T20:00:00Z', '2026-10-31T20:00:00.000Z', '2026-10-31T23:59:59+04:00', '2026-11-01T00:00:00+04:00', '2026-10-31T15:00:00-05:00', '2026-01-01T00:00:00Z',
    '2004-06-27T00:30:00Z', '2004-06-26T20:30:00Z', '2005-03-27T19:30:00Z', '1996-06-30T20:30:00Z', '2026-10-31', 'abc', '', '2024-02-29T22:00:00Z'];
  // Not here: '2026-02-30T10:00:00Z', which V8's Date.parse rolls over to 2 March and the port refuses (see the report).
  for (let i = 0; i < 200; i++) stamps.push(new Date(Date.UTC(2026, 0, 1) + int(r, 0, 365 * 24 * 3600) * 1000 + int(r, 0, 999)).toISOString());
  for (const at of stamps) f.add('tbilisiDay', { at }, () => report.tbilisiDay(at));
  for (const at of stamps.slice(16, 60)) f.add('todayInTbilisi', { now: at }, () => report.todayInTbilisi(new Date(at)));
  const STATUSES = ['opened', 'evidence_confirmed', 'demand_sent', 'accepted', 'disputed', 'rejected', 'partially_received', 'closed'];
  const RESP = ['accepted', 'disputed', 'rejected', null];
  const stampOn = (day: string) => `${day}T${String(int(r, 0, 23)).padStart(2, '0')}:${String(int(r, 0, 59)).padStart(2, '0')}:${String(int(r, 0, 59)).padStart(2, '0')}Z`;
  const statusHistory = (start: string) => Array.from({ length: int(r, 0, 4) }, (_, k) => ({ at: stampOn(dates.addDays(start, k * int(r, 0, 10))), value: k === 0 ? 'opened' : pick(r, STATUSES) }));
  const partyHistory = (start: string) => Array.from({ length: int(r, 0, 4) }, (_, k) => {
    const response = k === 0 ? null : pick(r, RESP);
    return { at: stampOn(dates.addDays(start, k * int(r, 0, 10))), value: { response, responseOn: response === null ? null : dates.addDays(start, int(r, -5, 40)), limit: chance(r, 0.2) ? null : BigInt(int(r, 1, 20) * 10000) } };
  });
  const DOCS: Record<string, string> = { claimed: 'demand_letter', confirmed: 'debtor_acceptance', received: 'bank_statement', cancelled: 'court_decision', reversal: 'correction' };
  const record = (m: Any, k: number) => ({ ...m, documentKind: DOCS[m.kind], documentNumber: m.kind === 'cancelled' ? '2/123-26' : null, documentDate: chance(r, 0.9) ? m.bookedOn : null, payerId: m.kind === 'received' ? pick(r, ['p1', 'p2', 'p9', '', null]) : null, note: k % 5 === 0 ? 'შენიშვნა' : null });
  const makeCase = (i: number) => {
    const start = randDate(r, '2026-09-01', '2026-11-30');
    const claim = { payments: [{ amount: 10_000_000n, deductibleWithheld: 0n, premiumOffset: 0n, netPaidGel: 10_000_000n, offsetGel: 0n, paidOn: '2026-01-01' }], others: [] };
    const ms = growCase(r, null, start, claim, int(r, 2, 12), `m${i}-`);
    const records = shuffle(r, ms.map(record));
    const caseRecord = { caseId: `case-${i}`, caseNumber: `RG${String(int(r, 1, 999)).padStart(3, '0')}`, claimId: `claim-${i}`, claimNumber: `CL-${i % 37}`, policyNumber: `MHL-${i}`, classCode: '5.1', openedOn: dates.addDays(start, -int(r, 0, 10)), statusNow: pick(r, STATUSES), basis: chance(r, 0.8) ? { kind: 'police_report', number: 'ოქმი 7', date: dates.addDays(start, -12) } : null, ...(chance(r, 0.8) ? { statusHistory: statusHistory(dates.addDays(start, -5)) } : {}) };
    const parties: Any[] = [];
    if (chance(r, 0.9)) parties.push({ id: 'p1', name: 'ბრალეული პირი', role: 'liable_person', limit: null, response: null, responseOn: null, ...(chance(r, 0.5) ? { history: partyHistory(start) } : {}) });
    if (chance(r, 0.6)) {
      const response = pick(r, RESP);
      parties.push({ id: 'p2', name: 'მზღვეველი', role: 'liability_insurer', limit: chance(r, 0.2) ? null : BigInt(int(r, 1, 20) * 10000), response, responseOn: response === null ? null : dates.addDays(start, int(r, -5, 40)), ...(chance(r, 0.7) ? { history: partyHistory(dates.addDays(start, -3)) } : {}) });
    }
    if (chance(r, 0.1)) parties.push({ ...parties[parties.length - 1] ?? { id: 'p1', name: 'ბრალეული პირი', role: 'liable_person', limit: null, response: null, responseOn: null }, id: 'p3' });
    if (chance(r, 0.1)) parties.unshift({ id: 'p4', name: 'Another Insurer', role: 'liability_insurer', limit: 50000n, response: 'accepted', responseOn: start });
    return { start, records, caseRecord, parties };
  };
  for (let i = 0; i < 250; i++) {
    const { start, records, caseRecord, parties } = makeCase(i);
    const asOf = dates.addDays(start, int(r, -15, 70));
    const today = chance(r, 0.05) ? '31.10.2026' : dates.addDays(start, int(r, -5, 80));
    f.add('caseCard', ser({ c: caseRecord, parties, movements: records, asOf, today }), () => report.caseCard(caseRecord, parties, records, asOf, today));
    f.add('inShowOrder', ser({ movements: records, asOf }), () => report.inShowOrder(records, asOf));
    f.add('statusOn', ser({ history: caseRecord.statusHistory, movements: records, asOf, openedOn: caseRecord.openedOn }), () => report.statusOn(caseRecord.statusHistory, records, asOf, caseRecord.openedOn));
    for (const p of parties) f.add('termsOn', ser({ history: p.history, asOf }), () => report.termsOn(p.history, asOf));
    f.add('onDate', ser({ history: caseRecord.statusHistory, asOf }), () => report.onDate(caseRecord.statusHistory, asOf));
    const from = dates.addDays(asOf, -int(r, 0, 40));
    f.add('movedIn', ser({ movements: records, from, to: asOf }), () => report.movedIn(records, from, asOf));
  }
  // A reversal of what is not on the case, and of a reversal.
  {
    const base = [{ id: 'c-1', kind: 'confirmed', amount: 100000n, bookedOn: '2026-10-01', reversesId: null, documentKind: 'debtor_acceptance', documentNumber: null, documentDate: '2026-10-01', payerId: null, note: null }];
    const orphan = [...base, { id: 'orphan', kind: 'reversal', amount: -60000n, bookedOn: '2026-10-10', reversesId: 'missing', documentKind: 'correction', documentNumber: null, documentDate: '2026-10-10', payerId: null, note: null }];
    const nullTarget = [...base, { id: 'orphan', kind: 'reversal', amount: -60000n, bookedOn: '2026-10-10', reversesId: null, documentKind: 'correction', documentNumber: null, documentDate: '2026-10-10', payerId: null, note: null }];
    const early = [...base, { id: 'x', kind: 'reversal', amount: -100000n, bookedOn: '2026-09-30', reversesId: 'c-1', documentKind: 'correction', documentNumber: null, documentDate: null, payerId: null, note: null }];
    const rec = { caseId: 'case-x', caseNumber: 'RGX', claimId: 'claim-x', claimNumber: 'CL-X', policyNumber: 'MHL-X', classCode: '5.1', openedOn: '2026-10-01', statusNow: 'accepted', basis: null };
    for (const ms of [orphan, nullTarget, early]) {
      f.add('inShowOrder', ser({ movements: ms, asOf: '2026-10-31' }), () => report.inShowOrder(ms, '2026-10-31'));
      f.add('caseCard', ser({ c: rec, parties: [], movements: ms, asOf: '2026-10-31', today: '2026-12-31' }), () => report.caseCard(rec, [], ms, '2026-10-31', '2026-12-31'));
    }
  }
  for (let i = 0; i < 150; i++) {
    const cases = Array.from({ length: int(r, 0, 5) }, (_, k) => { const c = makeCase(1000 + i * 10 + k); return { record: c.caseRecord, parties: c.parties, movements: c.records }; });
    const asOf = randDate(r, '2026-09-01', '2027-01-31');
    const outside = chance(r, 0.5) ? Array.from({ length: int(r, 1, 6) }, () => ({ claimNumber: `CL-${int(r, 1, 4)}`, kind: pick(r, ['subrogation', 'salvage']), receivedOn: randDate(r, '2026-07-01', '2027-01-31'), amount: chance(r, 0.2) ? -BigInt(int(r, 1, 10000)) : BigInt(int(r, 1, 100000)) })) : undefined;
    const options = { ...(chance(r, 0.4) ? { onlyWithBalance: true } : {}), ...(outside ? { outside } : {}) };
    f.add('reportOnDate', ser({ cases, asOf, options }), () => report.reportOnDate(cases, asOf, options));
  }
  for (const kind of ['month', 'quarter', 'year']) {
    for (const day of ['2026-02-14', '2028-02-14', '2026-11-30', '2026-04-01', '2026-06-15', '2100-12-31', '1900-02-01', '2000-02-29', '2026-02-30', '15.10.2026']) f.add('periodOf', { kind, day }, () => report.periodOf(kind, day));
    for (let i = 0; i < 100; i++) { const day = randDate(r, '1900-01-01', '2100-12-31'); f.add('periodOf', { kind, day }, () => report.periodOf(kind, day)); }
  }
  const periods: [string, string][] = [['2026-10-01', '2026-10-31'], ['2026-10-31', '2026-10-01'], ['2026-02-30', '2026-03-01'], ['2026-10-01', '2026-10-01'], ['', ''], ['2026-10-01', 'x']];
  for (let i = 0; i < 150; i++) periods.push([randDate(r, '2025-01-01', '2027-12-31'), chance(r, 0.1) ? '2026-13-01' : randDate(r, '2025-01-01', '2027-12-31')]);
  for (const [from, to] of periods) f.add('checkPeriod', { from, to }, () => report.checkPeriod(from, to));
  grandTotal += f.write();
}

console.log(`total: ${grandTotal} cases`);
