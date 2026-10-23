// Parity cases for the reports of slice S3: the old program's own report functions (src/lib/premium-report.ts buildPremiumReport,
// impairment-report.ts buildImpairmentReport / buildImpairmentTotalsExact, policy-premium-report.ts buildPolicyPremiumReport and
// bordereau.ts buildBordereau) run over fixed fictional worlds in a real PostgreSQL database with the old schema (its own
// migrations, its own triggers), through its own db.ts. Each world is a set of raw table rows made by a seeded PRNG: GEL, USD and
// EUR policies of several classes, cancellations and changes, instalments owed by clients, brokers and agents, receipts, late
// allocations and reversals, cessions to quota, surplus, excess-of-loss and facultative treaties, claims with reserves, payments,
// reversals, recoveries and their treaty shares, sparse stored rates (a day with none falls back to the last earlier one; the
// National Bank is never reached). The case file holds the rows and every report the old code gave; the C# test
// (tests/Insurance.Integration.Tests/Reports/ReportParityTests.cs) inserts the same rows into SQL Server, runs the new reports and
// requires the same figures. Fictional data only; it drops and recreates only databases named insurance_parity_s3*.
//
//   LEGACY_DIR=/path/to/Insurance-Software PG_ADMIN_URL=postgres://postgres:…@localhost:5432/postgres \
//   node tests/parity/generate/reports.ts
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { register } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const LEGACY = process.env.LEGACY_DIR ?? '/home/user/legacy-run';
const adminUrl = process.env.PG_ADMIN_URL;
if (!adminUrl) throw new Error('PG_ADMIN_URL is not set');
register(pathToFileURL(path.join(LEGACY, 'scripts/integration/hooks.ts')).href, pathToFileURL(path.join(LEGACY, 'scripts/integration/')).href);
const { default: postgres } = await import(pathToFileURL(path.join(LEGACY, 'node_modules/postgres/src/index.js')).href);
const legacy = (p: string) => import(pathToFileURL(path.join(LEGACY, p)).href);

// ---------------------------------------------------------------- the old schema, once, as a template database
const TEMPLATE = 'insurance_parity_s3_template';
const server = postgres(adminUrl, { ssl: 'require', max: 1, onnotice: () => {} });
const dbUrl = (name: string) => { const u = new URL(adminUrl); u.pathname = `/${name}`; return u; };
await server.unsafe(`drop database if exists ${TEMPLATE} with (force)`);
await server.unsafe(`create database ${TEMPLATE}`);
{
  const t = postgres(dbUrl(TEMPLATE).href, { ssl: 'require', max: 1, onnotice: () => {} });
  await t.unsafe(`create schema core;
    grant usage on schema core to core_app, core_reader;
    alter default privileges in schema core grant select, insert, update, delete on tables to core_app;
    alter default privileges in schema core grant usage, select on sequences to core_app;
    alter default privileges in schema core grant select on tables to core_reader;`);
  const dir = path.join(LEGACY, 'db/migrations');
  for (const file of readdirSync(dir).filter((f) => f.endsWith('.sql')).sort()) await t.begin((tx: any) => tx.unsafe(readFileSync(path.join(dir, file), 'utf8')));
  await t.end();
}

// The old db.ts keeps one pool on globalThis.__sql: it is a stand-in that forwards every call to the current world's database.
let current: any = null;
(globalThis as any).__sql = new Proxy(function sql() {}, {
  apply: (_t, _this, args) => current(...args),
  get: (_t, key) => (typeof current[key] === 'function' ? current[key].bind(current) : current[key]),
});
const fetched: string[] = [];
globalThis.fetch = (async (url: unknown) => { fetched.push(String(url)); throw new Error('the National Bank is not reached in the parity run'); }) as typeof fetch;

const { buildPremiumReport } = await legacy('src/lib/premium-report.ts');
const { buildImpairmentReport, buildImpairmentTotalsExact } = await legacy('src/lib/impairment-report.ts');
const { buildPolicyPremiumReport } = await legacy('src/lib/policy-premium-report.ts');
const { buildBordereau } = await legacy('src/lib/bordereau.ts');

// ---------------------------------------------------------------- a seeded world
function prng(seed: number) {
  let a = seed >>> 0;
  const next = () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  const int = (lo: number, hi: number) => lo + Math.floor(next() * (hi - lo + 1));
  const pick = <T>(xs: readonly T[]): T => xs[int(0, xs.length - 1)]!;
  const chance = (p: number) => next() < p;
  return { next, int, pick, chance };
}

type Row = Record<string, string | number | boolean | null>;
const DAY = 86_400_000;
const iso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
const ms = (d: string) => Date.parse(`${d}T00:00:00Z`);
const addDays = (d: string, n: number) => iso(ms(d) + n * DAY);
const days = (a: string, b: string) => Math.round((ms(b) - ms(a)) / DAY);
const addMonths = (d: string, n: number) => { const x = new Date(ms(d)); x.setUTCMonth(x.getUTCMonth() + n); return iso(x.getTime()); };
const minDate = (a: string, b: string) => (a < b ? a : b);
/** Two places, half away from zero, from integer cents arithmetic (the stored amount). */
const cents = (n: bigint) => { const neg = n < 0n; const a = neg ? -n : n; return `${neg ? '-' : ''}${a / 100n}.${String(a % 100n).padStart(2, '0')}`; };
const toCents = (s: string) => { const m = /^(-?)(\d+)\.(\d{2})$/.exec(s)!; const v = BigInt(m[2]!) * 100n + BigInt(m[3]!); return m[1] ? -v : v; };
/** a × b / d in cents, half away from zero (b and d integers). */
const mulDiv = (a: bigint, b: bigint, d: bigint) => { const p = a * b; const neg = p < 0n !== d < 0n; const q = ((p < 0n ? -p : p) * 2n + (d < 0n ? -d : d)) / (2n * (d < 0n ? -d : d)); return neg ? -q : q; };
/** cents × rate (6 places) → cents, half away from zero. */
const atRate = (c: bigint, rate: string) => mulDiv(c, BigInt(rate.replace('.', '')), 1_000_000n);

function world(seed: number) {
  const r = prng(seed);
  const hex = () => Array.from({ length: 32 }, () => '0123456789abcdef'[r.int(0, 15)]).join('');
  const uuid = () => { const h = hex(); return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20, 32)}`; };
  let clock = Date.parse('2025-12-01T08:00:00Z');
  const at = () => { clock += 60_000; return new Date(clock).toISOString(); };
  const t: Record<string, Row[]> = {};
  const all: { table: string; row: Row }[] = [];
  const add = (table: string, row: Row) => { (t[table] ??= []).push(row); all.push({ table, row }); return row; };

  // Sparse stored rates: every few days, month ends always; a missing day falls back to the last earlier one.
  const rateOf: Record<string, Map<string, string>> = { USD: new Map(), EUR: new Map() };
  for (let d = '2025-12-01'; d <= '2027-02-28'; d = addDays(d, 1)) {
    const monthEnd = addDays(d, 1).slice(8) === '01';
    if (!monthEnd && !r.chance(0.3)) continue;
    for (const [c, base] of [['USD', 2.65], ['EUR', 2.9]] as const) {
      const rate = (base + r.int(0, 3000) / 10000 + r.int(0, 99) / 1_000_000).toFixed(6);
      rateOf[c]!.set(d, rate);
      add('exchange_rate', { currency: c, rate_date: d, rate });
    }
  }
  const rateOn = (c: string, d: string) => {
    if (c === 'GEL') return '1.000000';
    let x = d;
    while (!rateOf[c]!.has(x)) x = addDays(x, -1);
    return rateOf[c]!.get(x)!;
  };

  const party = (who: { first: string; last: string } | { name: string }, n: number) => {
    const person = 'first' in who;
    return add('party', {
      id: uuid(), kind: person ? 'person' : 'organization', is_resident: true, tax_id: person ? `0100${seed % 1000}${String(n).padStart(4, '0')}`.slice(0, 11).padEnd(11, '7') : `4${String(seed % 1000).padStart(3, '0')}${String(n).padStart(5, '0')}`,
      foreign_id: null, country_code: 'GE', first_name: person ? who.first : null, last_name: person ? who.last : null, name: person ? null : who.name,
      legal_form: person ? null : 'შპს', is_public_entity: false, is_pep: false, is_group_member: false, sanctions_blocked: false, created_at: at(), updated_at: at(),
    }).id as string;
  };
  const holders = [
    party({ first: 'ნინო', last: 'ტესტაძე' }, 1), party({ first: 'გიორგი', last: 'მაგალითაძე' }, 2), party({ name: 'შპს სატესტო ტრანსი' }, 3),
    party({ name: 'შპს ნიმუში' }, 4), party({ first: 'ლევან', last: 'სანიმუშო' }, 5),
  ];
  const reinsurer = party({ name: 'სატესტო გადამზღვეველი' }, 6);
  const reinsurer2 = party({ name: 'მეორე გადამზღვეველი' }, 7);
  const brokerParty = party({ name: 'სატესტო ბროკერი' }, 8);
  const agentParty = party({ first: 'აგენტი', last: 'შუამავლიშვილი' }, 9);
  const insuredParty = party({ first: 'დაზღვეული', last: 'პირველი' }, 10);
  const agent = add('intermediary', { id: uuid(), party_id: agentParty, kind: 'agent', contract_no: 'AG-1', status: 'active', commission_percent: '10.00', created_at: at() }).id as string;
  const broker = add('intermediary', { id: uuid(), party_id: brokerParty, kind: 'broker', contract_no: 'BR-1', status: 'active', commission_percent: '7.50', created_at: at() }).id as string;

  const treaty = (number: string, kind: string, currency: string, classes: string[], terms: Row) => {
    const id = add('ri_treaty', {
      id: uuid(), number, name: `ხელშეკრულება ${number}`, reinsurer_party_id: number.endsWith('2') ? reinsurer2 : reinsurer, broker_party_id: null, kind,
      period_from: '2026-01-01', period_to: '2026-12-31', currency, ceded_percent: null, retention: null, lines: null, priority: null, treaty_limit: null, treaty_premium: null,
      commission_percent: '0.00', broker_percent: '0.00', status: 'active', created_at: at(), ...terms,
    }).id as string;
    for (const c of classes) add('ri_treaty_class', { treaty_id: id, class_code: c });
    return id;
  };
  const tQuota = treaty('RQ-1', 'quota_share', 'GEL', ['5.1'], { ceded_percent: '30.0000', commission_percent: '20.00', broker_percent: '2.50', broker_party_id: brokerParty });
  const tUsd = treaty('RQ-2', 'quota_share', 'USD', ['6.1', '13'], { ceded_percent: '25.0000', commission_percent: '15.00' });
  const tSurplus = treaty('RS-1', 'surplus', 'GEL', ['9.1'], { retention: '50000.00', lines: 3, commission_percent: '12.50' });
  const tXl = treaty('RX-1', 'excess_of_loss', 'USD', ['5.1'], { priority: '1000.00', treaty_limit: '5000.00', treaty_premium: `${r.int(800, 2400)}.${String(r.int(0, 99)).padStart(2, '0')}` });
  const tFac = treaty('RF-1', 'facultative', 'EUR', [], { ceded_percent: '40.0000', commission_percent: '10.00', period_to: '2027-12-31' });
  const treaties = { tQuota, tUsd, tSurplus, tXl, tFac };

  const classes = ['5.1', '6.1', '13', '9.1', '4'];
  const policies: { id: string; number: string; contract: string; holder: string; currency: string; rate: string; from: string; to: string; lines: { id: string; cls: string; premium: bigint; gel: bigint; issue: string; sum: bigint }[]; insts: { id: string; amount: bigint; due: string }[]; cancelled: string | null }[] = [];
  const count = r.int(10, 14);
  for (let i = 1; i <= count; i++) {
    const currency = r.pick(['GEL', 'GEL', 'USD', 'EUR']);
    const from = addDays('2026-01-01', r.int(0, 290));
    const to = r.chance(0.8) ? addMonths(from, 12) : addDays(from, r.int(90, 300));
    const rate = rateOn(currency, from);
    const holder = r.pick(holders);
    const contract = add('contract', { id: uuid(), number: `C-${seed}-${String(i).padStart(3, '0')}`, signed_on: addDays(from, -r.int(0, 10)), policyholder_id: holder, branch_id: null, created_at: at(), created_by: null }).id as string;
    const number = `P-${seed}-${String(i).padStart(3, '0')}`;
    const id = uuid();
    add('policy', {
      id, contract_id: contract, number, issued_on: addDays(from, -r.int(0, 5)), replaces_policy_id: null, effective_from: from, effective_to: to, status: 'active',
      cancelled_on: null, premium_currency: currency, premium_rate: rate, created_at: at(), segment: r.pick(['retail', 'corporate']), approved_at: at(),
    });
    let object: string | null = null;
    if (r.chance(0.5)) object = add('insured_object', { id: uuid(), policy_id: id, object_type: 'vehicle', description: `Toyota Prius ${String.fromCharCode(65 + (i % 26))}${i}-${seed % 100}`, attributes: '{}' }).id as string;
    const lineClasses = r.chance(0.35) ? [r.pick(classes), r.pick(classes)].filter((c, k, a) => a.indexOf(c) === k) : [r.pick(classes)];
    const lines = [];
    for (const cls of lineClasses) {
      const premium = BigInt(r.int(10_000, 400_000));
      const gel = atRate(premium, rate);
      const sum = BigInt(r.int(1_000_000, 30_000_000));
      const line = add('policy_line', { id: uuid(), policy_id: id, insured_object_id: object, class_code: cls, sum_insured: cents(sum), sum_insured_currency: currency, premium: cents(premium), premium_gel: cents(gel), is_fully_reinsured: false }).id as string;
      const bookedOn = r.chance(0.2) ? addDays(from, r.int(1, 40)) : from;
      const issue = add('premium_transaction', { id: uuid(), policy_line_id: line, kind: 'issue', amount: cents(premium), amount_gel: cents(gel), booked_on: bookedOn, cover_from: from, cover_to: to, created_at: at(), created_by: null, cancels_transaction_id: null }).id as string;
      lines.push({ id: line, cls, premium, gel, issue, sum });
    }
    if (r.chance(0.3)) add('policy_intermediary', { policy_id: id, intermediary_id: r.pick([agent, broker]), commission_percent: '10.00' });
    if (r.chance(0.2)) add('policy_party', { policy_id: id, party_id: insuredParty, role: r.pick(['insured', 'beneficiary']) });
    // The schedule: 1, 2 or 4 instalments of the whole premium, each owed by a client, a broker or an agent.
    const total = lines.reduce((s, l) => s + l.premium, 0n);
    const n = r.pick([1, 2, 4]);
    const insts = [];
    for (let k = 0; k < n; k++) {
      const part = k === n - 1 ? total - (total / BigInt(n)) * BigInt(n - 1) : total / BigInt(n);
      const due = addMonths(from, (12 / n) * k);
      insts.push({ id: add('installment', { id: uuid(), policy_id: id, due_on: due, amount: cents(part), debtor_kind: r.pick(['client', 'client', 'broker', 'agent']) }).id as string, amount: part, due });
    }
    policies.push({ id, number, contract, holder, currency, rate, from, to, lines, insts, cancelled: null });
  }

  // Cessions of the issue rows: the quota share on GEL 5.1, the USD quota on USD 6.1 / 13, the surplus on 9.1 by sum insured, one EUR
  // policy facultative. Each cession mirrors its row: share of the amount, commission of the ceded amount, the row's dates.
  const cessionOf = new Map<string, { treaty: string; share: string; id: string; percent: bigint; commission: bigint }>();
  const cede = (row: string, treatyId: string, sharePercent: bigint /* ×10000 */, commissionPercent: bigint /* ×100 */, amount: bigint, gel: bigint, booked: string, from: string, to: string, cancels: string | null) => {
    const a = mulDiv(amount, sharePercent, 1_000_000n);
    const g = mulDiv(gel, sharePercent, 1_000_000n);
    const id = add('premium_cession', {
      id: uuid(), premium_transaction_id: row, treaty_id: treatyId, share_percent: (Number(sharePercent) / 10000).toFixed(4), amount: cents(a), amount_gel: cents(g),
      commission: cents(mulDiv(a, commissionPercent, 10_000n)), commission_gel: cents(mulDiv(g, commissionPercent, 10_000n)), booked_on: booked, cover_from: from, cover_to: to,
      cancels_cession_id: cancels, created_at: at(), created_by: null,
    }).id as string;
    return id;
  };
  let facultativeDone = false;
  for (const p of policies) {
    for (const l of p.lines) {
      const booked = (t.premium_transaction!.find((x) => x.id === l.issue)!.booked_on as string);
      let plan: [string, bigint, bigint] | null = null;
      if (l.cls === '5.1' && p.currency === 'GEL') plan = [tQuota, 300_000n, 2000n];
      else if ((l.cls === '6.1' || l.cls === '13') && p.currency === 'USD') plan = [tUsd, 250_000n, 1500n];
      else if (l.cls === '9.1' && p.currency === 'GEL') {
        const sum = l.sum; // cents
        const retention = 5_000_000n;
        if (sum > retention) {
          const ceded = sum - retention < 3n * retention ? sum - retention : 3n * retention;
          plan = [tSurplus, mulDiv(ceded, 1_000_000n, sum), 1250n];
        }
      } else if (p.currency === 'EUR' && !facultativeDone) { plan = [tFac, 400_000n, 1000n]; facultativeDone = true; }
      if (!plan || booked > '2026-12-31' || booked < '2026-01-01') continue;
      const id = cede(l.issue, plan[0], plan[1], plan[2], l.premium, l.gel, booked, p.from, p.to, null);
      cessionOf.set(l.issue, { treaty: plan[0], share: '', id, percent: plan[1], commission: plan[2] });
    }
  }

  // Receipts and allocations: part or all of an instalment, at the day's rate; some allocated late; some reversed.
  const paidOf = new Map<string, bigint>();
  const receipt = (p: (typeof policies)[number], inst: (typeof policies)[number]['insts'][number], amount: bigint, on: string, bookedOn: string) => {
    const rate = rateOn(p.currency, on);
    const gel = atRate(amount, rate);
    const pay = add('payment', { id: uuid(), received_on: on, amount_gel: cents(gel), method: r.pick(['bank', 'cash', 'online']), payer_party_id: p.holder, reference: `R-${seed}`, created_at: at(), created_by: null, kind: 'receipt', reverses_payment_id: null }).id as string;
    add('payment_allocation', { id: uuid(), payment_id: pay, installment_id: inst.id, amount: cents(amount), amount_gel: cents(gel), rate, created_at: at(), booked_on: bookedOn, created_by: null });
    paidOf.set(inst.id, (paidOf.get(inst.id) ?? 0n) + amount);
    return { pay, amount, gel, rate };
  };
  for (const p of policies) {
    for (const inst of p.insts) {
      if (!r.chance(0.6) || inst.due > '2026-12-15') continue;
      const amount = r.chance(0.6) ? inst.amount : inst.amount / BigInt(r.int(2, 4));
      const on = addDays(inst.due, r.int(-5, 40));
      if (on > '2027-01-20') continue;
      const booked = r.chance(0.2) ? addDays(on, r.int(1, 20)) : on;
      const got = receipt(p, inst, amount, on, booked);
      if (r.chance(0.15)) {
        const back = addDays(booked, r.int(1, 30));
        const rev = add('payment', { id: uuid(), received_on: back, amount_gel: cents(-got.gel), method: 'bank', payer_party_id: null, reference: 'შებრუნება', created_at: at(), created_by: null, kind: 'reversal', reverses_payment_id: got.pay }).id as string;
        add('payment_allocation', { id: uuid(), payment_id: rev, installment_id: inst.id, amount: cents(-got.amount), amount_gel: cents(-got.gel), rate: got.rate, created_at: at(), booked_on: back, created_by: null });
        paidOf.set(inst.id, paidOf.get(inst.id)! - got.amount);
      }
    }
  }

  // Cancellations: from a day inside the cover, the unearned part of each line returned (its cession mirrored), the schedule reduced.
  for (const p of policies) {
    if (!r.chance(0.3)) continue;
    const c = addDays(p.from, r.int(10, Math.max(11, days(p.from, p.to) - 5)));
    if (c > '2026-12-20') continue;
    p.cancelled = c;
    const policyRow = t.policy!.find((x) => x.id === p.id)!;
    policyRow.status = 'cancelled';
    policyRow.cancelled_on = c;
    policyRow.cancel_reason = 'კლიენტის განცხადება';
    const booked = r.chance(0.3) ? addDays(c, r.int(1, 10)) : c;
    let refund = 0n;
    for (const l of p.lines) {
      const total = BigInt(days(p.from, p.to));
      const left = BigInt(days(c, p.to));
      const back = mulDiv(l.premium, left, total);
      const backGel = mulDiv(l.gel, left, total);
      refund += back;
      const row = add('premium_transaction', { id: uuid(), policy_line_id: l.id, kind: 'cancellation', amount: cents(-back), amount_gel: cents(-backGel), booked_on: booked, cover_from: c, cover_to: p.to, created_at: at(), created_by: null, cancels_transaction_id: l.issue }).id as string;
      const ceded = cessionOf.get(l.issue);
      if (ceded && booked <= '2026-12-31') cede(row, ceded.treaty, ceded.percent, ceded.commission, -back, -backGel, booked, c, p.to, ceded.id);
    }
    // The schedule reduced from the last instalment back, as far as what is not paid.
    for (const inst of [...p.insts].reverse()) {
      if (refund <= 0n) break;
      const open = inst.amount - (paidOf.get(inst.id) ?? 0n);
      const cut = open < refund ? open : refund;
      if (cut <= 0n) continue;
      add('installment_adjustment', { id: uuid(), installment_id: inst.id, booked_on: booked, change: cents(-cut), reason: 'გაუქმება', created_at: at(), created_by: null });
      refund -= cut;
    }
  }

  // Changes issued as their own policy under the same contract: a reduction (no schedule) or an addition (its own instalment).
  let changes = 0;
  for (const p of policies) {
    if (p.cancelled || changes >= 2 || !r.chance(0.4)) continue;
    const on = addDays(p.from, r.int(20, Math.max(21, days(p.from, p.to) - 20)));
    if (on > '2026-12-20') continue;
    changes++;
    const id = uuid();
    const l = p.lines[0]!;
    const total = BigInt(days(p.from, p.to));
    const left = BigInt(days(on, p.to));
    const up = r.chance(0.5);
    const delta = (up ? 1n : -1n) * mulDiv(l.premium / 5n, left, total);
    const deltaGel = atRate(delta, rateOn(p.currency, on));
    add('policy', {
      id, contract_id: p.contract, number: `${p.number}-1`, issued_on: on, replaces_policy_id: p.id, effective_from: on, effective_to: p.to, status: 'active', cancelled_on: null,
      premium_currency: p.currency, premium_rate: rateOn(p.currency, on), created_at: at(), segment: 'retail', approved_at: at(), change_note: 'დაფარვის ცვლილება',
    });
    const line = add('policy_line', { id: uuid(), policy_id: id, insured_object_id: null, class_code: l.cls, sum_insured: cents(l.sum), sum_insured_currency: p.currency, premium: cents(delta), premium_gel: cents(deltaGel), is_fully_reinsured: false }).id as string;
    add('premium_transaction', { id: uuid(), policy_line_id: line, kind: 'issue', amount: cents(delta), amount_gel: cents(deltaGel), booked_on: on, cover_from: on, cover_to: p.to, created_at: at(), created_by: null, cancels_transaction_id: null });
    if (up) add('installment', { id: uuid(), policy_id: id, due_on: on, amount: cents(delta), debtor_kind: 'client' });
  }

  // Claims: a loss on a line in cover, a reserve, payments and a reversal, a recovery; the treaties' shares fixed at registration.
  let claimNo = 0;
  for (const p of policies) {
    if (!r.chance(0.45)) continue;
    const l = r.pick(p.lines);
    const end = minDate(p.cancelled ?? p.to, '2026-12-10');
    if (end <= addDays(p.from, 3)) continue;
    const occurred = addDays(p.from, r.int(1, days(p.from, end) - 1));
    const reported = addDays(occurred, r.int(0, 10));
    claimNo++;
    const number = `CL-${seed}-${claimNo}`;
    const event = add('loss_event', { id: uuid(), number: `E-${number}`, policy_id: p.id, occurred_on: occurred, reported_on: reported, description: 'სატესტო შემთხვევა', created_at: at(), created_by: null }).id as string;
    const claim = add('claim', { id: uuid(), number, policy_id: p.id, policy_line_id: l.id, currency: p.currency, occurred_on: occurred, reported_on: reported, description: 'სატესტო ზარალი', status: r.pick(['registered', 'in_review', 'approved']), loss_event_id: event, created_at: at(), created_by: null }).id as string;
    if (l.cls === '5.1' && p.currency === 'GEL' && cessionOf.has(l.issue)) add('claim_cession', { claim_id: claim, treaty_id: tQuota, share_percent: '30.0000', priority: null, treaty_limit: null });
    else if ((l.cls === '6.1' || l.cls === '13') && p.currency === 'USD' && cessionOf.has(l.issue)) add('claim_cession', { claim_id: claim, treaty_id: tUsd, share_percent: '25.0000', priority: null, treaty_limit: null });
    else if (l.cls === '5.1' && p.currency === 'USD') add('claim_cession', { claim_id: claim, treaty_id: tXl, share_percent: '0.0000', priority: '1000.00', treaty_limit: '5000.00' });
    // Reserve and payments in the claim currency; every GEL value under the 10 000 GEL threshold.
    let reserve = BigInt(r.int(50_000, 300_000));
    add('claim_reserve', { id: uuid(), claim_id: claim, booked_on: reported, change: cents(reserve), reason: 'პირველადი შეფასება', created_at: at(), created_by: null });
    if (r.chance(0.4)) {
      const change = BigInt(r.int(-40_000, 60_000));
      reserve += change;
      add('claim_reserve', { id: uuid(), claim_id: claim, booked_on: addDays(reported, r.int(1, 30)), change: cents(change), reason: 'შეფასების შემდეგ', created_at: at(), created_by: null });
    }
    let paidOn = addDays(reported, r.int(5, 40));
    for (let k = 0; k < r.int(0, 2) && paidOn <= '2026-12-31'; k++) {
      const amount = reserve / BigInt(r.int(2, 3));
      const rate = rateOn(p.currency, paidOn);
      const gel = atRate(amount, rate);
      const pay = add('claim_payment', { id: uuid(), claim_id: claim, paid_on: paidOn, amount: cents(amount), deductible_withheld: '0.00', premium_offset: '0.00', net_paid: cents(amount), net_paid_gel: cents(gel), rate, payee_kind: 'policyholder', payee_party_id: p.holder, created_at: at(), created_by: null, kind: 'payment' }).id as string;
      add('claim_reserve', { id: uuid(), claim_id: claim, booked_on: paidOn, change: cents(-amount), reason: 'ანაზღაურება', created_at: at(), created_by: null });
      reserve -= amount;
      if (r.chance(0.2)) {
        const back = addDays(paidOn, r.int(1, 10));
        add('claim_payment', { id: uuid(), claim_id: claim, paid_on: back, amount: cents(-amount), deductible_withheld: '0.00', premium_offset: '0.00', net_paid: cents(-amount), net_paid_gel: cents(-gel), rate, payee_kind: 'policyholder', payee_party_id: p.holder, created_at: at(), created_by: null, kind: 'reversal', reverses_claim_payment_id: pay, reversal_reason: 'correction' });
        add('claim_reserve', { id: uuid(), claim_id: claim, booked_on: back, change: cents(amount), reason: 'შებრუნება', created_at: at(), created_by: null });
        reserve += amount;
      }
      paidOn = addDays(paidOn, r.int(10, 60));
    }
    if (r.chance(0.3)) add('claim_recovery', { id: uuid(), claim_id: claim, kind: r.pick(['salvage', 'subrogation']), received_on: addDays(reported, r.int(20, 80)), amount_gel: cents(BigInt(r.int(1_000, 50_000))), created_at: at(), created_by: null, row_kind: 'receipt' });
  }
  return { rows: all, treaties };
}

// ---------------------------------------------------------------- run the old reports over each world
const json = (v: unknown) => JSON.parse(JSON.stringify(v, (_k, x) => (typeof x === 'bigint' ? x.toString() : x)));
const months = (from: string, to: string) => { const out: string[] = []; for (let m = from; m <= to; m = addMonths(m, 1)) out.push(m.slice(0, 7)); return out; };

const cases = [];
for (const seed of [20261023, 20261024, 20261025]) {
  // The rows in the order they were made, as the actions would have written them: a receipt's allocation before its reversal.
  const { rows: ordered, treaties } = world(seed);
  const name = `insurance_parity_s3_${seed}`;
  await server.unsafe(`drop database if exists ${name} with (force)`);
  await server.unsafe(`create database ${name} template ${TEMPLATE}`);
  current = postgres(dbUrl(name).href, { ssl: 'require', max: 5, idle_timeout: 20, onnotice: () => {} });
  for (const { table, row } of ordered) {
    const cols = Object.keys(row);
    await current.unsafe(`insert into core.${table} (${cols.join(', ')}) values (${cols.map((_, i) => `$${i + 1}`).join(', ')})`, cols.map((c) => row[c]));
  }
  const calls: { fn: string; args: unknown[]; result: unknown }[] = [];
  for (const month of months('2026-01-01', '2027-01-01')) calls.push({ fn: 'premiumReport', args: [month], result: json(await buildPremiumReport(month)) });
  for (const d of ['2026-03-31', '2026-06-30', '2026-08-15', '2026-09-30', '2026-12-31', '2027-01-31']) {
    calls.push({ fn: 'impairment', args: [d], result: json(await buildImpairmentReport(d)) });
    calls.push({ fn: 'impairmentExact', args: [d], result: json(await buildImpairmentTotalsExact(d)) });
  }
  for (const [from, to] of [['2026-01-01', '2026-01-31'], ['2026-04-01', '2026-06-30'], ['2026-09-01', '2026-09-30'], ['2026-10-01', '2026-12-31'], ['2026-01-01', '2026-12-31'], ['2027-01-01', '2027-01-31']])
    calls.push({ fn: 'policyPremium', args: [from, to], result: json(await buildPolicyPremiumReport(from, to)) });
  for (const id of Object.values(treaties))
    for (const [from, to] of [['2026-01-01', '2026-03-31'], ['2026-04-01', '2026-06-30'], ['2026-07-01', '2026-09-30'], ['2026-10-01', '2026-12-31'], ['2026-01-01', '2026-12-31']])
      calls.push({ fn: 'bordereau', args: [id, from, to], result: json(await buildBordereau(id, from, to)) });
  await current.end();
  await server.unsafe(`drop database if exists ${name} with (force)`);
  cases.push({ seed, rows: ordered.map(({ table, row }) => ({ t: table, r: row })), calls });
  console.log(`world ${seed}: ${ordered.length} rows, ${calls.length} reports`);
}
await server.unsafe(`drop database if exists ${TEMPLATE} with (force)`);
await server.end();
writeFileSync(new URL('../cases/reports.json', import.meta.url), `${JSON.stringify({ legacyCommit: '17c1f1fb4b401a16ab5d0ba52d7992f5b8761e37', nationalBankRequests: fetched.length, worlds: cases }, null, 1)}\n`);
console.log(`National Bank requests refused: ${fetched.length}`);
