// Parity cases of the regulatory forms (src/lib/regulatory/*): the old TypeScript code run on deterministic inputs, its outputs written
// to tests/parity/cases/regulatory-*.json for the C# tests (tests/Insurance.Domain.Tests/Regulatory/RegulatoryParityTests.cs).
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/regulatory.ts
import { readFileSync } from 'node:fs';
import { failure, legacy, LEGACY, normalize, prng, sha, text, writeCases, type Rng } from './regulatory-common.ts';

const zip = await legacy('src/lib/regulatory/zip.ts');
const formula = await legacy('src/lib/regulatory/formula.ts');
const { TemplateWorkbook } = await legacy('src/lib/regulatory/workbook.ts');
const form6 = await legacy('src/lib/regulatory/form6.ts');
const annex = await legacy('src/lib/regulatory/financial-annex.ts');
const reserves = await legacy('src/lib/regulatory/reserves-placement.ts');
const { xlsxResponse } = await legacy('src/lib/regulatory/export.ts');
const { reinsuranceTexts } = await legacy('src/i18n/reinsurance.ts');

const template = (f: string) => readFileSync(`${LEGACY}/src/lib/regulatory/templates/${f}`);
const RESERVES = template('reserves-placement.xlsx'), N1 = template('financial-annex-n1.xlsx'), QUARTERLY = template('other-quarterly-forms.xlsx');
const TEMPLATES: Record<string, Buffer> = { reserves: RESERVES, n1: N1, quarterly: QUARTERLY };

/** An amount as decimal text: ordinary, zero, negative, near and beyond a double's 15–16 digits, odd but valid forms. */
function amountText(rng: Rng): string {
  const special = ['0', '0.00', '0.01', '-0.01', '90071992547408.01', '90071992547408.10', '-90071992547408.90', '9999999999999999.99', '99999999999999.99', '9999999999999.99',
    '45035996273704.05', '50000000000000.01', '1e3', ' 12.5 ', '1.5E-3', '100', '-0', '0.125', '123456789012.3456', '35184372088832.13', '7.5', '1e-7', '2.5E+2'];
  if (rng.chance(0.3)) return rng.pick(special);
  const digits = rng.int(1, 18);
  let i = String(rng.int(1, 9));
  for (let k = 1; k < digits; k++) i += String(rng.int(0, 9));
  const places = rng.pick([0, 1, 2, 2, 2, 3, 4]);
  let f = '';
  for (let k = 0; k < places; k++) f += String(rng.int(0, 9));
  return `${rng.chance(0.2) ? '-' : ''}${i}${places ? `.${f}` : ''}`;
}
/** Whole tetri (bigint) as text, for the reports. */
function tetri(rng: Rng): string {
  const t = amountText(rng).trim();
  if (/e/i.test(t)) return String(rng.int(0, 1_000_000));
  const neg = t.startsWith('-');
  const [w, f = ''] = t.replace('-', '').split('.');
  const v = BigInt(w! + f.padEnd(2, '0').slice(0, 2));
  return (neg && v !== 0n ? -v : v).toString();
}
const tetriBig = (s: string) => BigInt(s);

const valueView = (v: any): [string, string | null] => [v.t, v.t === 'empty' ? null : v.t === 'num' ? formula.decimalText(v) : v.t === 'str' ? v.s : String(v.b)];
const xView = (x: unknown) => (x === null ? null : [typeof x, typeof x === 'number' ? String(x) : String(x)]);

/** Every formula cell's value, exactly and as Excel holds it; only the non-zero ones listed, all of them hashed. */
function formulaValues(wb: any) {
  const all: [string, unknown, unknown][] = [];
  for (const name of wb.sheetNames()) {
    for (const [ref, c] of (wb as any).sheets.get(name).cells) {
      if (!c.f) continue;
      let exact: unknown, excel: unknown;
      try { exact = valueView(wb.value(name, ref)); } catch (e) { exact = failure(e); }
      try { excel = xView(wb.excelValue(name, ref)); } catch (e) { excel = failure(e); }
      all.push([`${name}!${ref}`, exact, excel]);
    }
  }
  const shown = all.filter(([, e]) => !(Array.isArray(e) && (e[1] === '0' || e[1] === null)));
  return { count: all.length, sha: sha(JSON.stringify(all)), shown };
}

function outputs(wb: any, base: Buffer, filename = 'f.xlsx') {
  const out: any = {};
  try { out.problems = wb.precisionProblems(); } catch (e) { out.problems = failure(e); }
  try { out.workbook = normalize(zip, wb.toBuffer(), base); } catch (e) { out.workbook = failure(e); }
  try {
    const r = xlsxResponse(wb, filename);
    out.response = { status: r.status, type: r.headers.get('content-type'), disposition: r.headers.get('content-disposition') };
  } catch (e) { out.response = failure(e); }
  out.values = formulaValues(wb);
  return out;
}

// ---- Formula: exact numbers, parsing and both evaluations.
function formulaCases() {
  const rng = prng(20261022);
  const fractions: [string, string, string][] = [];
  for (let i = 0; i < 400; i++) {
    const n = BigInt(rng.int(-1e9, 1e9)) * (rng.chance(0.3) ? 10n ** BigInt(rng.int(0, 12)) : 1n);
    const d = rng.chance(0.5) ? 10n ** BigInt(rng.int(0, 13)) : BigInt(rng.int(1, 999)) * (rng.chance(0.5) ? 2n ** BigInt(rng.int(0, 20)) : 1n);
    fractions.push([n.toString(), d.toString(), formula.decimalText(formula.num(n, d))]);
  }
  const decimals: [string, unknown][] = [];
  const odd = ['', ' ', '1.', '.5', '+1', '1e', '1e+', '- 1', '1,5', 'abc', '0x10', '1_000', '١٢', '1e400', '-0.0', '00012.3400', '1E-12', '  -7  ', ' 5 ', '5\n'];
  for (const s of [...odd, ...Array.from({ length: 200 }, () => amountText(rng))]) {
    try { decimals.push([s, formula.decimalText(formula.decimal(s))]); } catch (e) { decimals.push([s, failure(e)]); }
  }
  // Formulas over a small grid of values (numbers, texts, booleans, empties) on two sheets.
  const grid: Record<string, any> = {};
  const xgrid: Record<string, unknown> = {};
  const sheetsOf = ['S', 'Other Sheet'];
  for (const sh of sheetsOf) for (let r = 1; r <= 6; r++) for (let c = 1; c <= 5; c++) {
    const key = `${sh}!${c},${r}`;
    const k = rng.int(0, 9);
    if (k < 5) { const t = amountText(rng).trim(); grid[key] = { t: 'num', text: t }; xgrid[key] = Number(t); }
    else if (k < 7) { const s = rng.pick(['', ' ', 'x', '12', 'abc', '1.5', 'TRUE']); grid[key] = { t: 'str', s }; xgrid[key] = s; }
    else if (k < 8) { const b = rng.chance(0.5); grid[key] = { t: 'bool', b }; xgrid[key] = b; }
  }
  const exactCells = { value: (sh: string, col: number, row: number) => { const g = grid[`${sh}!${col},${row}`]; return !g ? formula.EMPTY : g.t === 'num' ? formula.decimal(g.text) : g.t === 'str' ? { t: 'str', s: g.s } : { t: 'bool', b: g.b }; } };
  const doubleCells = { value: (sh: string, col: number, row: number) => (xgrid[`${sh}!${col},${row}`] ?? null) as any };
  const ref = () => { const c = formula.colLetters(rng.int(1, 5)), r = rng.int(1, 6); return `${rng.chance(0.2) ? '$' : ''}${c}${rng.chance(0.2) ? '$' : ''}${r}`; };
  const sheetRef = () => (rng.chance(0.25) ? (rng.chance(0.5) ? "'Other Sheet'!" : 'S!') : '');
  const expr = (depth: number): string => {
    const k = rng.int(0, depth > 2 ? 3 : 11);
    switch (k) {
      case 0: return amountText(rng).trim().replace(/^-/, '').replace(/^\./, '0.');
      case 1: return sheetRef() + ref();
      case 2: return `"${rng.pick(['', 'a', 'ab c', '12', 'x&y'])}"`;
      case 3: return sheetRef() + ref();
      case 4: return `SUM(${sheetRef()}${ref()}:${ref()}${rng.chance(0.5) ? `,${expr(depth + 1)}` : ''})`;
      case 5: return `IF(${expr(depth + 1)},${expr(depth + 1)}${rng.chance(0.6) ? `,${expr(depth + 1)}` : ''})`;
      case 6: return `-${expr(depth + 1)}`;
      case 7: return `(${expr(depth + 1)})`;
      default: return `${expr(depth + 1)}${rng.pick([' + ', '-', '*', '/', '&', '=', '<>', '<', '>=', '<=', '>', ' ^ '])}${expr(depth + 1)}`;
    }
  };
  const formulas: unknown[] = [];
  const bad = ['', '=', 'SUM(', 'A1:', "'Unclosed!A1", '"open', 'FOO(1)', 'A1 B1', 'S!', 'IF(1)', 'IF(1,2,3,4)', '1+', 'A1:B2', 'SUM(A1:B2', '((1)', 'a1', '=$$A1', 'SUM()', 'SUM(1,)'];
  for (const f of [...bad, ...Array.from({ length: 500 }, () => expr(0))]) {
    const c: any = { f, dr: rng.int(0, 2), dc: rng.int(0, 1) };
    try {
      const node = formula.parseFormula(f);
      try { c.exact = valueView(formula.evaluate(node, 'S', exactCells, c.dr, c.dc)); } catch (e) { c.exact = failure(e); }
      try { c.excel = xView(formula.evaluateDouble(node, 'S', doubleCells, c.dr, c.dc)); } catch (e) { c.excel = failure(e); }
    } catch (e) { c.parse = failure(e); }
    formulas.push(c);
  }
  const cols = Array.from({ length: 60 }, () => rng.int(1, 18278)).map((n) => [n, formula.colLetters(n), formula.colNumber(formula.colLetters(n))]);
  // JavaScript's own number texts, which the forms write and compare (String(x), Number(text)).
  const doubles: [string, string][] = [];
  const specialDoubles = [0, -0, 1, -1, 0.1, 0.2, 0.30000000000000004, 1e21, 1e-7, 1.5e-7, 123456789012345680000, 1e20, 9007199254740993, 5e-324, 1.7976931348623157e308, 0.000001, 1234.5678, -1e-6, 100, 1e300, 2 ** 53, 0.1 + 0.7];
  for (const x of [...specialDoubles, ...Array.from({ length: 300 }, () => { const m = rng.next() * 10 - 5; return m * 10 ** rng.int(-25, 25); }), ...Array.from({ length: 100 }, () => Number(amountText(rng)))]) doubles.push([Buffer.from(new Float64Array([x]).buffer).toString('hex'), String(x)]);
  const numbers: [string, string][] = [];
  for (const s of [...odd, 'Infinity', '-Infinity', '+Infinity', 'infinity', '0b101', '0o17', '0X1f', '-0x10', '1e-400', '.', '+', '1.e5', '.e5', '  3 ﻿', ...Array.from({ length: 100 }, () => amountText(rng))]) numbers.push([s, String(Number(s))]);
  return { fractions, decimals, formulas, cols, doubles, numbers, grid };
}

// ---- The workbook itself: random fillings of the three templates.
function workbookCases() {
  const rng = prng(7_10_2026);
  const cases: unknown[] = [];
  for (let n = 0; n < 36; n++) {
    const which = (['reserves', 'reserves', 'quarterly', 'quarterly', 'n1', 'reserves'] as const)[n % 6]!;
    const base = TEMPLATES[which]!;
    const wb = TemplateWorkbook.open(base);
    const sheets = wb.sheetNames() as string[];
    const cellsOf = (s: string) => [...(wb as any).sheets.get(s).cells.values()] as any[];
    const ops: unknown[] = [];
    const steps = rng.int(1, which === 'n1' ? 25 : 40);
    for (let k = 0; k < steps; k++) {
      const sheet = rng.chance(0.6) ? sheets[0]! : rng.pick(sheets);
      const cells = cellsOf(sheet);
      const kind = rng.pick(['num', 'num', 'num', 'num', 'text', 'text', 'create', 'wrap', 'wrap', 'height', 'insert', 'titles']);
      const op: any = { kind, sheet };
      try {
        if (kind === 'num') {
          const inputs = cells.filter((c) => !c.f);
          op.ref = rng.chance(0.9) && inputs.length ? rng.pick(inputs).ref : rng.pick(cells).ref;
          op.text = amountText(rng);
          wb.setNumber(sheet, op.ref, op.text);
        } else if (kind === 'text') {
          op.ref = rng.pick(cells).ref;
          op.text = text(rng, 12);
          wb.setText(sheet, op.ref, op.text);
        } else if (kind === 'create') {
          op.ref = `${formula.colLetters(rng.int(1, 30))}${rng.int(1, 60)}`;
          op.text = text(rng, 4);
          wb.setText(sheet, op.ref, op.text, { create: true });
        } else if (kind === 'wrap') {
          op.ref = rng.pick(cells).ref;
          wb.wrapText(sheet, op.ref);
        } else if (kind === 'height') {
          op.row = rng.int(0, 70);
          op.points = rng.pick([0, 410, 15, 30, 409, 12.75, 409.5, 27, -1]);
          wb.setRowHeight(sheet, op.row, op.points);
        } else if (kind === 'insert') {
          op.after = rng.int(0, 50); op.count = rng.int(0, 4); op.styleRow = rng.int(1, 50); op.growMerges = rng.chance(0.5);
          wb.insertRows(sheet, op.after, op.count, op.styleRow, { growMerges: op.growMerges });
        } else {
          op.first = rng.int(0, 10); op.last = op.first + rng.int(-1, 3);
          wb.setPrintTitles(sheet, op.first, op.last);
        }
      } catch (e) { op.result = failure(e); }
      ops.push(op);
    }
    cases.push({ template: which, ops, ...outputs(wb, base) });
  }
  // Rows inserted first (the only time they can be), then filled: form 6's grid grown and the rows below.
  for (let n = 0; n < 12; n++) {
    const wb = TemplateWorkbook.open(QUARTERLY);
    const ops: unknown[] = [];
    for (let k = 0; k < rng.int(1, 4); k++) {
      const op: any = { kind: 'insert', sheet: form6.FORM6, after: rng.pick([8, 10, 12, 13, 37, 38, 40, 44]), count: rng.int(0, 30), styleRow: rng.pick([9, 10, 13, 37, 38]), growMerges: rng.chance(0.7) };
      try { wb.insertRows(op.sheet, op.after, op.count, op.styleRow, { growMerges: op.growMerges }); } catch (e) { op.result = failure(e); }
      ops.push(op);
    }
    for (let k = 0; k < 15; k++) {
      const op: any = { kind: 'text', sheet: form6.FORM6, ref: `${rng.pick(['A', 'B', 'C', 'H'])}${rng.int(9, 60)}`, text: text(rng, 30) };
      try { wb.setText(op.sheet, op.ref, op.text); wb.wrapText(op.sheet, op.ref); op.wrap = true; } catch (e) { op.result = failure(e); }
      ops.push(op);
    }
    cases.push({ template: 'quarterly', ops, ...outputs(wb, QUARTERLY) });
  }
  return cases;
}

// ---- Reserves placement (forms 11–14) and form 1 (annex N1) from the reports.
const CLASS_CODES = ['2', '3.1', '3.2', '4', '5', '5.1', '5.2', '6', '6.1', '6.2', '6.3', '7', '8', '9.1', '9.2', '10', '11.1', '11.2', '12', '13', '14', '15.1', '15.2', '15.3', '16', '17.1', '17.2', '17.3', '18', '1.1', '19', '3', '99'];
function report(rng: Rng, to: string) {
  const classes = [], claims = [];
  for (let k = 0; k < rng.int(0, 12); k++) classes.push({ classCode: rng.pick(CLASS_CODES), unearnedGel: tetri(rng), cededUnearnedGel: tetri(rng) });
  for (let k = 0; k < rng.int(0, 12); k++) claims.push({ classCode: rng.pick(CLASS_CODES), rbnsGel: tetri(rng), reinsurerRbnsGel: tetri(rng), ibnrGel: tetri(rng) });
  return { to, classes, claims, totals: { unearnedGel: tetri(rng), cededUnearnedGel: tetri(rng) }, claimsTotals: { rbnsGel: tetri(rng), reinsurerRbnsGel: tetri(rng), ibnrGel: tetri(rng) } };
}
/** The report as the old code takes it: bigints, and the fields it does not read set to nothing. */
function asPremiumReport(r: any) {
  const zero = { policiesIssued: 0, statisticalWrittenGel: 0n, financialWrittenGel: 0n, earnedGel: 0n, cededWrittenGel: 0n, riCommissionGel: 0n };
  const z2 = { paidGel: 0n, recoveredGel: 0n, reinsurerPaidGel: 0n };
  return {
    month: r.to.slice(0, 7), from: `${r.to.slice(0, 8)}01`, to: r.to, receivables: [], treatyPremiumGel: 0n,
    classes: r.classes.map((c: any) => ({ ...zero, classCode: c.classCode, className: '', unearnedGel: tetriBig(c.unearnedGel), cededUnearnedGel: tetriBig(c.cededUnearnedGel) })),
    claims: r.claims.map((c: any) => ({ ...z2, classCode: c.classCode, className: '', rbnsGel: tetriBig(c.rbnsGel), reinsurerRbnsGel: tetriBig(c.reinsurerRbnsGel), ibnrGel: tetriBig(c.ibnrGel) })),
    totals: { ...zero, unearnedGel: tetriBig(r.totals.unearnedGel), cededUnearnedGel: tetriBig(r.totals.cededUnearnedGel) },
    claimsTotals: { ...z2, rbnsGel: tetriBig(r.claimsTotals.rbnsGel), reinsurerRbnsGel: tetriBig(r.claimsTotals.reinsurerRbnsGel), ibnrGel: tetriBig(r.claimsTotals.ibnrGel) },
  };
}
const figuresView = (f: any) => ({ C: f.C.toString(), D: f.D.toString(), E: f.E.toString(), F: f.F.toString(), G: f.G.toString() });
const COMPANIES = ['სატესტო კომპანია (გამოგონილი)', '', '  ', 'A$&B', "x$'y", '$1 ltd', '__underscored__', ' padded ', 'ს.ს. "დაზღვევა" <test> & co', 'ძალიან გრძელი სახელი '.repeat(12)];
const DAYS = ['2026-10-31', '2026-12-31', '2025-02-28', '2028-02-29', '2026-01-31', 'bad', '2026-1'];

function reservesCases() {
  const rng = prng(1110);
  const cases: unknown[] = [];
  for (let n = 0; n < 40; n++) {
    const r = report(rng, rng.pick(DAYS));
    const company = rng.pick(COMPANIES);
    const pr = asPremiumReport(r);
    const c: any = { report: r, company };
    c.figures = [...reserves.form11Figures(pr)].map(([code, f]: any) => [code, figuresView(f)]);
    c.unplaced = reserves.unplacedClasses(pr).map((u: any) => [u.code, figuresView(u.figures)]);
    try {
      const { workbook, date } = reserves.fillReservesPlacement(RESERVES, pr, company);
      c.date = date;
      c.form11 = reserves.FORM11_ALL_ROWS.map(([, row]: any) => reserves.FORM11_COLUMNS.map((col: string) => reserves.shown(workbook.value(reserves.FORM11, `${col}${row}`))));
      Object.assign(c, outputs(workbook, RESERVES));
    } catch (e) { c.fill = failure(e); }
    cases.push(c);
  }
  return cases;
}

function annexCases() {
  const rng = prng(1_4_7);
  const cases: unknown[] = [];
  const kinds = ['client', 'broker', 'agent', 'all', 'other'];
  for (let n = 0; n < 24; n++) {
    const day = rng.pick(DAYS.slice(0, 5));
    const r = report(rng, day);
    const impairment = { asOf: rng.chance(0.9) ? day : '2026-10-30', totals: Array.from({ length: rng.int(0, 5) }, () => ({ kind: rng.pick(kinds), policies: rng.int(0, 9), unpaidPremiumGel: tetri(rng), reserveGel: tetri(rng) })) };
    const settings: Record<string, string> = {};
    for (const key of ['company_name', 'company_address', 'company_phone', 'company_email']) if (rng.chance(0.8)) settings[key] = rng.pick([...COMPANIES, '+995 32 2 00 00 00', 'info@example.ge']);
    const c: any = { report: r, impairment, settings };
    const imp = { asOf: impairment.asOf, totals: impairment.totals.map((t) => ({ ...t, unpaidPremiumGel: BigInt(t.unpaidPremiumGel), reserveGel: BigInt(t.reserveGel) })) };
    c.n14 = Object.entries(annex.n14Figures(imp)).map(([k, v]: any) => [k, v.toString()]);
    c.n17 = Object.entries(annex.n17Figures(asPremiumReport(r))).map(([k, v]: any) => [k, v.toString()]);
    try {
      const wb = annex.fillFinancialAnnex(N1, asPremiumReport(r), imp, settings);
      Object.assign(c, outputs(wb, N1));
    } catch (e) { c.fill = failure(e); }
    cases.push(c);
  }
  return cases;
}

// ---- Form 6.
const KINDS = ['quota_share', 'surplus', 'excess_of_loss', 'facultative', 'stop_loss'];
function longName(rng: Rng, n: number) {
  if (rng.chance(0.3)) return 'x'.repeat(n);
  return 'Fictional Broker Company '.repeat(Math.ceil(n / 25)).slice(0, n).trimEnd();
}
function source(rng: Rng, size: 'small' | 'grid' | 'over' | 'huge') {
  const nClasses = { small: rng.int(0, 4), grid: 6, over: rng.int(7, 12), huge: 12 }[size];
  const perClass = { small: rng.int(1, 3), grid: rng.int(3, 5), over: rng.int(1, 9), huge: 25 }[size];
  const classes = Array.from({ length: nClasses }, (_, i) => {
    const code = rng.pick([`${i + 1}`, `${i + 1}.${rng.int(1, 3)}`, `${10 + i}`]);
    return { code, name_ka: rng.chance(0.1) ? '' : `სახეობა ${code} ${rng.chance(0.2) ? text(rng, 6) : ''}`, sort_order: rng.chance(0.2) ? 100 : (i + 1) * 100 };
  });
  const treaties: any[] = [], treatyClasses: any[] = [], placedClasses: any[] = [];
  let id = 0;
  for (const cls of classes) {
    for (let k = 0; k < perClass; k++) {
      const t = `t${id++}`;
      const from = rng.pick(['2026-01-01', '2026-12-31', '2025-06-01', '2027-01-01', '2026-07-01']);
      const to = rng.pick(['2026-12-31', '2026-12-30', '2027-12-30', '2027-05-31', '2026-12-31']);
      const big = size === 'huge' ? rng.chance(0.05) : rng.chance(0.15);
      treaties.push({
        id: t, number: `RI-${String(rng.int(0, 999)).padStart(4, '0')}`, kind: rng.pick(KINDS), period_from: from, period_to: to,
        reinsurer_rating: rng.chance(0.5) ? rng.pick(['AA-', 'A+', ' BBB ', '']) : null, rating_agency: rng.chance(0.5) ? rng.pick(['S&P', 'AM Best', "Moody's"]) : null,
        reinsurer: big ? longName(rng, rng.pick([300, 600, 1200, 3000])) : `Re ${cls.code}/${k} ${text(rng, 3)}`,
        reinsurer_country: rng.chance(0.6) ? rng.pick(['DE', 'CH', 'GB', ' GE ']) : null,
        broker: rng.chance(0.4) ? (big && rng.chance(0.5) ? longName(rng, rng.pick([539, 600, 1200, 5000])) : text(rng, 8)) : null,
        broker_country: rng.chance(0.3) ? 'GB' : null,
      });
      if (rng.chance(0.9)) treatyClasses.push({ treaty_id: t, class_code: cls.code });
      if (rng.chance(0.3)) placedClasses.push({ treaty_id: t, class_code: rng.pick(classes).code });
      if (rng.chance(0.1)) treatyClasses.push({ treaty_id: t, class_code: rng.pick(classes).code });
    }
  }
  if (rng.chance(0.2)) treatyClasses.push({ treaty_id: 't0', class_code: 'unknown' });
  return { treaties, treatyClasses, placedClasses, classes };
}
const rowView = (r: any) => ({ classCode: r.classCode, classLabel: r.classLabel, cells: r.cells });

function form6Cases() {
  const rng = prng(6_2026);
  const K = reinsuranceTexts.kinds;
  const cases: unknown[] = [];
  const sizes = ['small', 'small', 'grid', 'grid', 'over', 'over', 'over', 'huge'] as const;
  for (let n = 0; n < 32; n++) {
    const s = source(rng, sizes[n % sizes.length]!);
    const asOf = rng.pick(['2026-12-31', '2026-12-30', '2027-01-01', '2026-06-30']);
    const company = rng.pick(COMPANIES);
    const c: any = { source: s, asOf, company };
    const rows = form6.form6Rows(s, asOf, K);
    c.rows = rows.map(rowView);
    c.overflow = form6.form6Overflow(rows);
    try { Object.assign(c, outputs(form6.fillForm6(QUARTERLY, rows, asOf, company), QUARTERLY)); } catch (e) { c.fill = failure(e); }
    cases.push(c);
  }
  // The pure parts: wrapped lines, pieces, quarters, codes.
  const texts: unknown[] = [];
  for (let n = 0; n < 300; n++) {
    const t = rng.chance(0.2) ? longName(rng, rng.int(1, 6000)) : rng.chance(0.1) ? '' : text(rng, 60);
    const width = rng.pick([...Object.values(form6.FORM6_WIDTHS), 1, 0.5, 5, 100]) as number;
    const maxLines = rng.pick([27, 27, 1, 3, 10]);
    texts.push({ text: t, width, maxLines, lines: form6.wrappedLines(t, width), pieces: form6.textPieces(t, width, maxLines) });
  }
  const quarters = ['2026-Q1', '2026-Q2', '2026-Q3', '2026-Q4', '2028-Q1', '1900-Q1', '2100-Q4', '1899-Q4', '2101-Q1', '0099-Q4', '2026-Q0', '2026-Q5', '2026Q4', '26-Q4', '2026-q4', '', ' 2026-Q4', '2026-Q4\n', '٢٠٢٦-Q4']
    .map((q) => [q, form6.quarterEnd(q)]);
  const months = ['2026-01', '2026-03', '2026-04', '2026-12', '2026-13', '2026', '2026-00', 'abcd-xy', '2026-1', '', '2026-07-15'].map((m) => [m, form6.quarterOf(m)]);
  const codes = ['1', '5.1', '13', '17.3', '5.', '.5', '1.2.3', '', '123', '0.0', 'a.b'].map((c) => [c, form6.formCode(c)]);
  // The order of names the form sorts (localeCompare): mixed scripts, cases, digits, punctuation.
  const names = ['Munich Re', 'munich re', 'Swiss Re', '007 Re', '=1+1 Re', 'Ä Re', 'a Re', 'B Re', 'ბ Re', 'ა Re', 'Ⴀ Re', 'Re 10', 'Re 9', 'Re-1', 'Re 1', 're_1', '"A"', "'A'", '(A)', '', ' ', 'RI-0001', 'RI-001', 'ri-0002', 'Ž', 'Z', 'ž', 'é', 'e', 'f', 'ﬀ', 'ß', 'ss', 'Ré', 'Ré'];
  const order = names.map((a) => names.map((b) => Math.sign(a.localeCompare(b))));
  return { kinds: K, cases, texts, quarters, months, codes, names, order };
}

writeCases('regulatory-formula.json', formulaCases());
writeCases('regulatory-workbook.json', workbookCases());
writeCases('regulatory-reserves.json', reservesCases());
writeCases('regulatory-annex.json', annexCases());
writeCases('regulatory-form6.json', form6Cases());
