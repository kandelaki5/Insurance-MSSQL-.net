// Parity cases for the pure modules of src/lib (close deadline, form dates, months, numbering, phones, query parameters,
// permissions, passwords) and for the JavaScript string behaviour the port reproduces (trim, toUpperCase, Number):
// deterministic inputs, boundaries included, run through the old code; the C# tests in tests/Insurance.Domain.Tests/Common
// require the same answers.
//
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/common.ts
import fs from 'node:fs';
import path from 'node:path';
import { scryptSync } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { legacy, rng } from './common-rng.ts';

const close = await legacy('src/lib/close-deadline.ts');
const dateText = await legacy('src/lib/date-text.ts');
const month = await legacy('src/lib/month.ts');
const numbering = await legacy('src/lib/numbering.ts');
const phone = await legacy('src/lib/phone.ts');
const known = await legacy('src/lib/known.ts');
const permissions = await legacy('src/lib/permissions.ts');
const password = await legacy('src/lib/password.ts');

const r = rng(20261023);
const WS = [' ', '\t', '\n', ' ', '﻿', '\u0085', ' ', '　', '​', '\r'];

// ── close deadline ──────────────────────────────────────────────────────────────────────────────────────────────────
const parseDays: unknown[] = [];
for (const t of ['', '0', '1', '5', '9', '10', '31', '32', '-3', '3.5', 'five', '100', '07a', '07', '00', ' 6 ', ' 6﻿', '\u00856', '٣', '1e1', '+5', null]) {
  parseDays.push({ in: t, out: close.parseCloseWithinDays(t) ?? null });
}
for (let i = 0; i < 200; i++) {
  const t = r.text(['0', '1', '2', '3', '9', ' ', 'a', '-', '.', ...WS], 0, 4);
  parseDays.push({ in: t, out: close.parseCloseWithinDays(t) ?? null });
}
const withinDays: unknown[] = [];
for (const s of [{}, { 'accounting.close_within_days': '8' }, { 'accounting.close_within_days': 'x' }, { 'accounting.close_within_days': ' 31 ' }, { other: '3' }]) {
  withinDays.push({ in: s, out: close.closeWithinDays(s) });
}
const dueDates: unknown[] = [];
for (let i = 0; i < 600; i++) {
  const m = `${r.int(1990, 2060)}-${String(r.int(1, 12)).padStart(2, '0')}-01`;
  const days = r.int(1, 31);
  const today = new Date(Date.parse(`${close.closeDueDate(m, days)}T00:00:00Z`) + r.int(-2, 2) * 86_400_000).toISOString().slice(0, 10);
  dueDates.push({ in: { month: m, days, today }, out: { due: close.closeDueDate(m, days), overdue: close.isCloseOverdue(m, today, days) } });
}

// ── form dates ──────────────────────────────────────────────────────────────────────────────────────────────────────
const D = ['0', '1', '2', '3', '4', '5', '9', '/', '.', '-', ' ', 'a', '٣', '\n'];
const isoToText: unknown[] = [];
const textToIso: unknown[] = [];
const typing: unknown[] = [];
const caret: unknown[] = [];
for (const t of ['2026-10-14', '2026-01-05', '', '14/10/2026', '2026-1-5', '2026-13-45', '0000-00-00', '2026-10-14\n', '٢٠٢٦-10-14']) isoToText.push({ in: t, out: dateText.isoToText(t) });
for (const t of ['14/10/2026', '05/01/2026', '5/1/2026', '14.10.2026', '14-10-2026', ' 01/09/2026 ', '01/13/2026', '31/09/2026', '29/02/2026', '29/02/2028', '14/10/26', '10/14/2026', '', '2026-10-14', '01/01/0050', '01/01/0000', '01/01/0100', '29/02/0400', '29/02/1900', '31/12/9999', '0/1/2026', '1/0/2026', '14/10/2026\n', '﻿14/10/2026', '\u008514/10/2026', '14/10.2026', '٣/1/2026']) {
  textToIso.push({ in: t, out: dateText.textToIso(t) });
}
for (let i = 0; i < 1500; i++) {
  const t = r.bool(0.5) ? r.text(D, 0, 12) : `${r.int(0, 35)}${r.pick(['/', '.', '-'])}${r.int(0, 14)}${r.pick(['/', '.', '-'])}${String(r.int(0, 2200)).padStart(r.pick([2, 4, 4, 4]), '0')}`;
  isoToText.push({ in: t, out: dateText.isoToText(t) });
  textToIso.push({ in: t, out: dateText.textToIso(t) });
  const typed = r.text(D, 0, 14);
  const shown = dateText.typingMask(typed);
  typing.push({ in: typed, out: shown });
  const c = r.int(-3, typed.length + 2);
  caret.push({ in: { shown, typed, caret: c }, out: dateText.caretAfter(shown, typed, c) });
}
for (let i = 0; i < 300; i++) {
  const d = r.date('1899-01-01', '2101-12-31');
  isoToText.push({ in: d, out: dateText.isoToText(d) });
  const t = dateText.isoToText(d);
  textToIso.push({ in: t, out: dateText.textToIso(t) });
}

// ── months ──────────────────────────────────────────────────────────────────────────────────────────────────────────
const monthStart: unknown[] = [];
const monthName: unknown[] = [];
for (const v of ['2026-01-01', '2026-12-01', '1900-01-01', '2100-12-01', '0000-01-01', '2026-00-01', '2026-13-01', '2026-02-30', '2026-02-02', '1899-12-01', '2101-01-01', 'abc', '', null, '2026-01-01\n', '٢٠٢٦-01-01']) {
  monthStart.push({ in: v, out: month.isMonthStart(v) });
}
for (let i = 0; i < 400; i++) {
  const v = r.bool(0.6) ? `${r.int(1890, 2110)}-${String(r.int(0, 13)).padStart(2, '0')}-${r.pick(['01', '01', '02', '31'])}` : r.text(['1', '2', '0', '-', 'a', ' '], 0, 11);
  monthStart.push({ in: v, out: month.isMonthStart(v) });
}
for (const v of ['2026-04-01', '2026-01-01', '2026-12-31', '2026-13-01', '2026-00-01', '2026- 1-01', '2026-1.-01', '2026-.5-01', '2026', '', 'abcd-ef', '2026-0x-01', '2026-+4-01', '2026-1e-01', '2026- 7-01']) {
  monthName.push({ in: v, out: month.monthNameKa(v) });
}

// ── numbering ───────────────────────────────────────────────────────────────────────────────────────────────────────
const PREFIXES = ['', 'MHL', 'MHL1', 'MHL0', 'MHL-', 'CAS', 'CAS0', 'CAS1', 'C', '1', '12', 'mhl', 'MHL 1', 'ABCDEFGHIJ', 'ABCDEFGHIJK', 'A-1', 'ÄB', '٣'];
const formatCases: unknown[] = [];
const overlapCases: unknown[] = [];
const clashCases: unknown[] = [];
const checkCases: unknown[] = [];
for (let i = 0; i < 1200; i++) {
  const prefix = r.pick(PREFIXES);
  const digits = r.int(0, 13);
  const value = r.pick([0, 1, 9, 10, 58, 999, 1000, 99999, 100000, 999999, 1000000, 1651821, 10000000, -5, 999999999999, 1000000000000, r.int(0, 10 ** 7)]);
  formatCases.push({ in: { prefix, digits, value }, out: numbering.formatNumber(prefix, digits, value) });
  const d2 = r.pick([digits, 2.5, -1, 0, 1, 12, 13, 6]);
  checkCases.push({ in: { prefix, digits: d2, value }, out: numbering.checkSeries(prefix, d2, value) });
  const a = { prefix: r.pick(PREFIXES), digits: r.int(1, 12), next: BigInt(r.pick([1, 2, 20, 50000, 100000, r.int(1, 10 ** 6)])) };
  const b = { prefix: r.pick(PREFIXES), digits: r.int(1, 12), next: BigInt(r.pick([1, 2, 20, 50000, 100000, r.int(1, 10 ** 6)])) };
  if (r.bool(0.5)) b.digits = Math.max(1, a.prefix.length + a.digits - b.prefix.length);
  overlapCases.push({ in: { a: { ...a, next: Number(a.next) }, b: { ...b, next: Number(b.next) } }, out: numbering.formatsOverlap(a, b) });
  clashCases.push({ in: { a: { prefix: a.prefix, digits: a.digits }, b: { prefix: b.prefix, digits: b.digits } }, out: numbering.kindsClash(a, b) });
}

// ── phones ──────────────────────────────────────────────────────────────────────────────────────────────────────────
const P = ['+', '9', '9', '5', '5', '0', '1', '3', '4', ' ', '-', '(', ')', '.', 'a', '٣', ' ', '\n'];
const CODES = ['', '+995', '995', '+49', '49', '1', '+1', '+12345', '+ 9 9 5', 'abc', '+٣'];
const normalizeCases: unknown[] = [];
const splitCases: unknown[] = [];
const formatPhoneCases: unknown[] = [];
const STORED = ['595534555', '+995 555 00 00 00', '+49 301234567', '+49301234567', '+49 30 1234567', '+995 595534555', '+995595534555', '+1 202-555-0143', '+995 ...', '', '   ', null, '+49 30\n1234', '+49-30-12', '+4930 12', '995595534555', '+995', 'abc', '+', '+1 ', '﻿595534555', '+49 30 1234567'];
for (let i = 0; i < 1500; i++) {
  const code = r.pick(CODES);
  const number = r.bool(0.4) ? r.text(P, 0, 18) : r.pick(['595 53-45-55', '(595) 534555', '+995 595 534 555', '995595534555', '59553455', '5955345551', '30 1234567', '202-555-0143', '123456', '123', '1234567890123456', '+49301234567', '+4912', '+1 202-555-0143', '+995' + r.text(['5', '9', '1'], 7, 10), ' +995 595534555']);
  normalizeCases.push({ in: { code, number }, out: phone.normalizePhone(code, number) });
  const stored = r.bool(0.5) ? r.pick(STORED) : r.text(P, 0, 18);
  splitCases.push({ in: stored, out: phone.splitPhone(stored) });
  formatPhoneCases.push({ in: stored, out: phone.formatPhone(stored) });
}

// ── query parameters ────────────────────────────────────────────────────────────────────────────────────────────────
const knownCases: unknown[] = [];
const map = { done: 'დაიხურა', forbidden: 'უფლება არ აქვს', '': 'ცარიელი' };
for (const key of ['done', 'forbidden', '', '__proto__', 'constructor', 'toString', 'hasOwnProperty', 'valueOf', 'unknown', 'DONE', null]) {
  knownCases.push({ in: { map, key }, out: known.known(map, key) ?? null });
}
const singleCases: unknown[] = [];
for (let i = 0; i < 50; i++) {
  const params: Record<string, string | string[] | undefined> = {};
  for (let k = r.int(0, 4); k > 0; k--) params[r.pick(['approve', 'advance', 'gel', 'month', 'q'])] = r.pick([undefined, 'x', '', ['x', 'y'], ['a', 'b', 'c']]);
  singleCases.push({ in: Object.fromEntries(Object.entries(params).map(([k, v]) => [k, v === undefined ? null : v])), out: Object.fromEntries(Object.entries(known.singleParams(params)).map(([k, v]) => [k, v ?? null])) });
}

// ── permissions ─────────────────────────────────────────────────────────────────────────────────────────────────────
const USERS = ['user-anna', 'user-beka', ''];
const ROLES = [...permissions.ROLE_CODES, 'chief_accountant', '', 'Admin'];
const KEYS = [...Object.keys(permissions.PERMISSIONS), 'premium.receipt.delete', 'claim.register', '', '__proto__', 'constructor', 'toString'];
const GROUPS = ['grp-cashiers', 'grp-sales-desk', 'g-valid', '', 'grp-Cashiers', 'a', 'B'];
const DECISIONS = ['allow', 'deny', 'allow', 'deny', 'ALLOW', 'inherit', '', 'yes'];
const evaluateCases: unknown[] = [];
const evaluateAllCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const subject = { userId: r.pick(USERS), roleCode: r.pick(ROLES) };
  const permission = r.bool(0.7) ? r.pick(KEYS.slice(0, 3)) : r.pick(KEYS);
  const overrides = Array.from({ length: r.pick([0, 0, 1, 1, 2, 3]) }, () => ({ userId: r.pick(USERS), permission: r.bool(0.6) ? permission : r.pick(KEYS), decision: r.pick(DECISIONS) }));
  const groups = r.bool(0.2) ? null : {
    memberships: Array.from({ length: r.int(0, 4) }, () => ({ userId: r.bool(0.7) ? subject.userId : r.pick(USERS), groupId: r.pick(GROUPS) })),
    grants: Array.from({ length: r.int(0, 4) }, () => ({ groupId: r.pick(GROUPS), permission: r.bool(0.7) ? permission : r.pick(KEYS) })),
  };
  evaluateCases.push({ in: { subject, permission, overrides, groups }, out: permissions.evaluatePermission(subject, permission, overrides, groups ?? undefined) });
  if (i % 5 === 0) evaluateAllCases.push({ in: { subject, overrides, groups }, out: permissions.evaluateAll(subject, overrides, groups ?? undefined) });
}

// ── passwords ───────────────────────────────────────────────────────────────────────────────────────────────────────
// Hashes made by the old code (random salts) must verify in C#; keys derived with fixed salts must be byte-for-byte equal.
const PASSWORDS = ['', 'a', 'password', 'Tbilisi-2026!', 'პაროლი-სადაზღვევო', 'გამარჯობა 😀 ñ ß', ' spaced ', 'x'.repeat(200), '\ud800lone', '$scrypt$'];
const hashCases: unknown[] = [];
for (const p of PASSWORDS) {
  const stored = await password.hashPassword(p);
  hashCases.push({ password: p, stored, verifies: await password.verifyPassword(p, stored), otherVerifies: await password.verifyPassword(p + 'x', stored) });
}
const deriveCases: unknown[] = [];
for (const [p, saltHex, keylen] of [['', '00000000000000000000000000000000', 64], ['password', '73616c74', 64], ['პაროლი', 'ffeeddccbbaa99887766554433221100', 64], ['a', '', 64], ['გამარჯობა 😀', '0102', 32], ['x', '01', 1], ['x', '01', 100]] as const) {
  deriveCases.push({ password: p, saltHex, keylen, key: scryptSync(p, Buffer.from(saltHex, 'hex'), keylen).toString('hex') });
}
const verifyCases: unknown[] = [];
const sample = hashCases[3] as { stored: string };
for (const [p, stored] of [
  ['Tbilisi-2026!', sample.stored], ['tbilisi-2026!', sample.stored], ['Tbilisi-2026!', sample.stored.toUpperCase()], ['Tbilisi-2026!', sample.stored + '$extra'],
  ['Tbilisi-2026!', sample.stored.replace('scrypt', 'bcrypt')], ['x', ''], ['x', 'scrypt'], ['x', 'scrypt$'], ['x', 'scrypt$aa'], ['x', 'scrypt$aa$'], ['x', 'scrypt$$aa'],
  ['anything', 'scrypt$aa$zz'], ['anything', 'scrypt$zz$ab'], ['x', 'scrypt$aa$a'], ['Tbilisi-2026!', sample.stored.slice(0, -1)], ['Tbilisi-2026!', sample.stored.slice(0, -2)],
  ['Tbilisi-2026!', sample.stored.replace(/\$([0-9a-f]{32})\$/, '$$$1z$$')],
] as const) {
  verifyCases.push({ password: p, stored, verifies: await password.verifyPassword(p, stored) });
}

// ── JavaScript string behaviour ─────────────────────────────────────────────────────────────────────────────────────
// Every code point whose toUpperCase() is not itself, and trim()/Number() on texts built from JavaScript's and .NET's
// white space.
const upper: [number, string][] = [];
for (let cp = 0; cp <= 0x10ffff; cp++) {
  if (cp >= 0xd800 && cp <= 0xdfff) continue;
  const s = String.fromCodePoint(cp);
  const u = s.toUpperCase();
  if (u !== s) upper.push([cp, u]);
}
const trimCases: unknown[] = [];
const numberCases: unknown[] = [];
for (let i = 0; i < 300; i++) {
  const t = r.text([...WS, 'a', '1', ' '], 0, 6);
  trimCases.push({ in: t, out: t.trim() });
}
for (const t of ['', ' ', '6', '1.5', '-2.5', '0', '-0', '0x10', '0X1f', '0b11', '0o7', '0x', '-0x10', '1e3', '1E-3', '1e+2', 'abc', 'Infinity', '+Infinity', '-Infinity', 'infinity', '-3', ' 7 ', ' 7', '﻿8', '\u00858', '1_0', '12abc', '.5', '5.', '.', '+4', '+-4', '1e300', '1e400', '-1e400', '99999999999999999999', '0.1', '00012', '1.2.3', '١٢', '  3 　', '0x1fffffffffffff', '1e', 'e5']) {
  const v = Number(t);
  numberCases.push({ in: t, out: Number.isNaN(v) ? 'NaN' : v === Infinity ? 'Infinity' : v === -Infinity ? '-Infinity' : Object.is(v, -0) ? '-0' : String(v) });
}

const cases = {
  source: 'src/lib/{close-deadline,date-text,month,numbering,phone,known,permissions,password}.ts and JavaScript strings',
  parseCloseWithinDays: parseDays,
  closeWithinDays: withinDays,
  closeDueDate: dueDates,
  isoToText,
  textToIso,
  typingMask: typing,
  caretAfter: caret,
  isMonthStart: monthStart,
  monthNameKa: monthName,
  formatNumber: formatCases,
  checkSeries: checkCases,
  formatsOverlap: overlapCases,
  kindsClash: clashCases,
  normalizePhone: normalizeCases,
  splitPhone: splitCases,
  formatPhone: formatPhoneCases,
  known: knownCases,
  singleParams: singleCases,
  evaluatePermission: evaluateCases,
  evaluateAll: evaluateAllCases,
  catalogue: permissions.catalogue(),
  passwordHashes: hashCases,
  scryptKeys: deriveCases,
  verifyPassword: verifyCases,
  toUpperCase: upper,
  trim: trimCases,
  number: numberCases,
};
const out = path.join(path.dirname(fileURLToPath(import.meta.url)), '../cases/common.json');
fs.writeFileSync(out, JSON.stringify(cases) + '\n');
console.log(out, Object.fromEntries(Object.entries(cases).filter(([k]) => k !== 'source').map(([k, v]) => [k, (v as unknown[]).length])));
