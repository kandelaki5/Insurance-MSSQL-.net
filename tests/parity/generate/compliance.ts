// Parity cases for src/compliance/*.ts (client risk, KYC, UN sanctions screening, FMS suspension): many deterministic
// inputs, boundaries included, run through every exported function of the old code; the C# tests in
// tests/Insurance.Domain.Tests/Compliance require the same answers.
//
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/compliance.ts
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { legacy, rng } from './common-rng.ts';

const risk = await legacy('src/compliance/client-risk.ts');
const kyc = await legacy('src/compliance/kyc.ts');
const sanctions = await legacy('src/compliance/sanctions.ts');
const suspension = await legacy('src/compliance/suspension.ts');

const r = rng(20261022);
const attempt = <T>(fn: () => T): { out: T } | { throws: true } => {
  try { return { out: fn() }; } catch { return { throws: true }; }
};

// ── client risk ─────────────────────────────────────────────────────────────────────────────────────────────────────
const NUMBER_TEXTS = ['', ' ', '6', '1.5', '-2.5', '0', '0x10', '0b11', '0o7', '1e3', '1e-3', 'abc', 'Infinity', '-Infinity', '-3', ' 7 ', ' 7', '﻿8', '1_0', '12abc', '.5', '5.', '+4', '2', '3', '365', '30', '15000', '49999.99', '1e300', '99999999999999999999'];
const COUNTRY_TEXTS = ['', 'ir, kp;af', 'IR', 'ir kp', ' de ;; fr ,', 'IRN, I, ir1, IR', 'ß, ıq', 'ру, ir', 'ir kp﻿af', 'ir\u0085kp', 'gb\tus\nca'];
const settingsKeys = ['aml.premium_point_one_gel', 'aml.premium_point_two_gel', 'aml.sanctions_check_max_age_days', 'aml.medium_score', 'aml.high_score', 'aml.review_months_high', 'aml.review_months_medium', 'aml.review_months_low'];

function randomSettings(): Record<string, string> {
  const s: Record<string, string> = {};
  if (r.bool(0.7)) s['aml.high_risk_countries'] = r.pick(COUNTRY_TEXTS);
  for (const key of settingsKeys) if (r.bool(0.4)) s[key] = r.pick(NUMBER_TEXTS);
  if (r.bool(0.1)) s['aml.unrelated'] = 'x';
  return s;
}

const settingsCases: unknown[] = [];
const policies: unknown[] = [null];
for (let i = 0; i < 300; i++) {
  const settings = randomSettings();
  const policy = risk.riskPolicyFromSettings(settings);
  settingsCases.push({ in: settings, out: policy });
  if (i < 60) policies.push(policy);
}
settingsCases.push({ in: {}, out: risk.riskPolicyFromSettings({}) });
settingsCases.push({ in: { 'aml.high_risk_countries': 'ir, kp;af', 'aml.review_months_high': '6', 'aml.high_score': '' }, out: risk.riskPolicyFromSettings({ 'aml.high_risk_countries': 'ir, kp;af', 'aml.review_months_high': '6', 'aml.high_score': '' }) });

const COUNTRIES = ['GE', 'DE', 'IR', 'ir', 'KP', 'kp', 'AF', 'ß', 'Ir', 'ıR', ''];
const PREMIUMS = [0, 0.01, 999.99, 1000, 1000.01, 2500, 3000, 3000.01, 4999.99, 5000, 5000.01, 14999.99, 15000, 15000.01, 20000, 44999.99, 45000, 49999.99, 50000, 60000, 150000];
const SHARES = [null, 0, 25, 49.99, 50, 100];

function randomFacts(asOf: string) {
  const near = () => new Date(Date.parse(`${asOf}T00:00:00Z`) - r.int(-30, 800) * 86_400_000).toISOString().slice(0, 10);
  return {
    kind: r.pick(['person', 'organization']),
    isResident: r.bool(0.7),
    countryCode: r.pick(COUNTRIES),
    isPep: r.bool(0.15),
    isPublicEntity: r.bool(0.2),
    stateSharePercent: r.pick(SHARES),
    verifiedOn: r.bool(0.7) ? near() : null,
    sanctionsCheckedOn: r.bool(0.75) ? (r.bool(0.2) ? new Date(Date.parse(`${asOf}T00:00:00Z`) - r.pick([364, 365, 366, 367]) * 86_400_000).toISOString().slice(0, 10) : near()) : null,
    annualPremiumGel: r.pick(PREMIUMS),
    previousYearPremiumGel: r.pick([0, 0, 1000, 5000, 6666.67, 15000, 20000, 55000]),
  };
}

const ASOF_EDGES = ['2026-10-03', '2026-01-31', '2028-02-29', '2025-12-31', '2027-03-31', '2026-08-31'];
const assessCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const asOf = i < 300 ? r.pick(ASOF_EDGES) : r.date('2015-01-01', '2035-12-31');
  const facts = randomFacts(asOf);
  const policy = r.pick(policies);
  assessCases.push({ in: { facts, asOf, policy }, ...attempt(() => risk.assessClientRisk(facts, asOf, policy ?? undefined)) });
}

const reviewCases: unknown[] = [];
for (let i = 0; i < 400; i++) {
  const level = r.pick(['low', 'medium', 'high']);
  const on = i < 100 ? r.pick(ASOF_EDGES) : r.date('2015-01-01', '2035-12-31');
  const policy = r.pick(policies);
  reviewCases.push({ in: { level, on, policy }, ...attempt(() => risk.reviewDueFor(level, on, policy ?? undefined)) });
}

// ── KYC ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
const KYC_THRESHOLDS = [null, { reducedMaxGel: 500, verificationFreeMaxGel: { person: 2000, organization: 4000 }, ownerVerificationDeferralDays: 10 }];
const kycCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const asOf = r.date('2024-01-01', '2028-12-31');
  const around = () => new Date(Date.parse(`${asOf}T00:00:00Z`) - r.int(-10, 90) * 86_400_000).toISOString().slice(0, 10);
  const owners = Array.from({ length: r.pick([0, 0, 1, 1, 2, 3]) }, () => ({ identifiedOn: r.bool(0.8) ? around() : null, verifiedOn: r.bool(0.5) ? around() : null }));
  const input = {
    kind: r.pick(['person', 'organization']),
    annualPremiumGel: r.pick(PREMIUMS),
    hasLifePolicy: r.bool(0.1),
    identifiedOn: r.bool(0.6) ? around() : null,
    verifiedOn: r.bool(0.5) ? around() : null,
    relationshipFrom: r.bool(0.85) ? around() : null,
    owners,
    selfDeclarationOn: r.bool(0.6) ? around() : null,
  };
  const thresholds = r.pick(KYC_THRESHOLDS);
  kycCases.push({ in: { input, asOf, thresholds }, out: kyc.kycStatus(input, asOf, thresholds ?? undefined) });
}

// ── sanctions ───────────────────────────────────────────────────────────────────────────────────────────────────────
const LETTERS = [...'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ', ...'აბგდევზთიკლმნოპჟრსტუფქღყშჩცძწჭხჯჰ', ...'éèüöäßçñøåłİıĞşÉÜÖ', ...'абвгдежзАБВ', ' ', ' ', ' ', '-', '.', ',', '„', '“', '"', "'", '0', '1', '7', '9', ' ', '\t', '́', 'K', 'ﬁ', 'Ⅳ', '①', '😀', '\ud800', 'ǅ', 'ẞ'];
const WORDS = ['giorgi', 'beridze', 'გიორგი', 'ბერიძე', 'შპს', 'ss', 'LLC', 'Ltd', 'Company', 'al', 'the', 'Mohammed', 'Mohamed', 'Muhammad', 'Rashid', 'Rashed', 'Allied', 'Democratic', 'Forces', 'Démocratiques', 'Alliées', 'Eric', 'Badege', 'Erik', 'ivan', 'petrov', 'petrova', 'ალიანსი', 'ჯგუფი', 'Group', 'Holding', 'Bank', 'of', 'and', 'AB', 'X', 'Abu', 'Bakr', 'Bakar'];
const phrase = (min: number, max: number) => Array.from({ length: r.int(min, max) }, () => r.pick(WORDS)).join(r.pick([' ', ' ', ', ', '-', '  ']));

const translitCases: unknown[] = [];
const tokenCases: unknown[] = [];
const documentCases: unknown[] = [];
for (let i = 0; i < 600; i++) {
  const t = r.bool(0.5) ? r.text(LETTERS, 0, 30) : phrase(0, 5);
  translitCases.push({ in: t, out: sanctions.transliterateGeorgian(t) });
  tokenCases.push({ in: t, out: sanctions.nameTokens(t) });
  const d = r.text([...'AB12 3456-/.abcxyzß', 'ﬀ', 'ı', 'ſ', 'ǰ', 'ŉ', '٣', ' '], 0, 14);
  documentCases.push({ in: d, out: sanctions.normalizeDocument(d) });
}
for (const t of ['გიორგი ბერიძე', 'შპს „ალიანსი“ ჯგუფი', 'Forces Démocratiques', '', 'K Kelvin', 'İstanbul', 'ΑΣ Σίσυφος']) {
  tokenCases.push({ in: t, out: sanctions.nameTokens(t) });
}

const xmlEscape = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
function entryXml(kind: 'INDIVIDUAL' | 'ENTITY', id: number): string {
  const name = () => {
    const w = r.pick(WORDS.filter((x) => /^[A-Za-zéèüöä]+$/.test(x)));
    return r.bool(0.1) ? `${w.slice(0, 1)}&#233;${w.slice(1)}` : r.bool(0.05) ? `&#x${w.charCodeAt(0).toString(16)};${w.slice(1)}` : xmlEscape(w);
  };
  const parts: string[] = [];
  if (r.bool(0.95)) parts.push(`<DATAID>${r.bool(0.03) ? '' : 690000 + id}</DATAID>`);
  parts.push(`<FIRST_NAME>${r.bool(0.03) ? ' ' : name()}</FIRST_NAME>`);
  if (r.bool(0.8)) parts.push(`<SECOND_NAME>${name()}</SECOND_NAME>`);
  if (r.bool(0.3)) parts.push(`<THIRD_NAME>${name()}</THIRD_NAME>`);
  if (r.bool(0.1)) parts.push(`<FOURTH_NAME>${r.bool(0.5) ? '' : name()}</FOURTH_NAME>`);
  parts.push(`<UN_LIST_TYPE>${r.pick(['DRC', 'Al-Qaida', 'DPRK', 'Libya'])}</UN_LIST_TYPE>`);
  if (r.bool(0.9)) parts.push(`<REFERENCE_NUMBER>${r.pick(['CDi', 'QDi', 'KPi', 'QDe'])}.${String(id).padStart(3, '0')}</REFERENCE_NUMBER>`);
  if (r.bool(0.8)) parts.push(`<LISTED_ON>${r.date('2001-01-01', '2026-01-01')}${r.pick(['', '-04:00', '-05:00', 'T00:00:00'])}</LISTED_ON>`);
  if (r.bool(0.05)) parts.push('<LISTED_ON>unknown</LISTED_ON>');
  for (let n = r.int(0, 2); n > 0; n--) parts.push(`<NATIONALITY><VALUE>${r.pick(['Iraq', 'Democratic Republic of the Congo', 'Georgia', ''])}</VALUE></NATIONALITY>`);
  for (let n = r.int(0, 3); n > 0; n--) {
    const alias = r.bool(0.2) ? '' : Array.from({ length: r.int(1, 4) }, name).join(' ');
    parts.push(`<${kind}_ALIAS><QUALITY>${r.pick(['Good', 'Low', 'a.k.a.'])}</QUALITY>${alias ? `<ALIAS_NAME>${alias}</ALIAS_NAME>` : '<ALIAS_NAME/>'}</${kind}_ALIAS>`);
  }
  for (let n = r.int(0, 2); n > 0; n--) {
    parts.push(`<INDIVIDUAL_DATE_OF_BIRTH>${r.bool(0.5) ? `<YEAR>${r.int(1940, 2000)}</YEAR>` : `<DATE>${r.date('1940-01-01', '2000-12-31')}</DATE>`}${r.bool(0.2) ? `<YEAR>${r.int(1940, 2000)}</YEAR>` : ''}</INDIVIDUAL_DATE_OF_BIRTH>`);
  }
  for (let n = r.int(0, 2); n > 0; n--) {
    parts.push(`<INDIVIDUAL_DOCUMENT><TYPE_OF_DOCUMENT>Passport</TYPE_OF_DOCUMENT><NUMBER>${r.text([...'AB12 345-6/xy'], 3, 10)}</NUMBER></INDIVIDUAL_DOCUMENT>`);
  }
  return `<${kind}>\n      ${parts.join('\n      ')}\n    </${kind}>`;
}
const parseCases: unknown[] = [];
const lists: unknown[] = [];
for (let i = 0; i < 40; i++) {
  const individuals = Array.from({ length: r.int(0, 25) }, (_, k) => entryXml('INDIVIDUAL', i * 100 + k));
  const entities = Array.from({ length: r.int(0, 10) }, (_, k) => entryXml('ENTITY', i * 100 + 50 + k));
  const xml = `<?xml version="1.0" encoding="UTF-8"?>\n<CONSOLIDATED_LIST${r.bool(0.9) ? ` dateGenerated="2026-10-${String(r.int(1, 28)).padStart(2, '0')}T23:00:05.487Z"` : ''}>\n  <INDIVIDUALS>\n    ${individuals.join('\n    ')}\n  </INDIVIDUALS>\n  <ENTITIES>\n    ${entities.join('\n    ')}\n  </ENTITIES>\n</CONSOLIDATED_LIST>`;
  const parsed = sanctions.parseUnList(xml);
  parseCases.push({ in: xml, out: parsed });
  lists.push(parsed.entries);
}
for (const xml of ['', '<x>', '<INDIVIDUAL><DATAID>1</DATAID><FIRST_NAME>A &amp;lt; B &#38;amp; &quot;C&quot; &apos;D&apos; &#x1F600;</FIRST_NAME></INDIVIDUAL>', '<ENTITY><DATAID> 7 </DATAID><FIRST_NAME>\n  ACME  \n</FIRST_NAME><INDIVIDUAL_DOCUMENT><NUMBER>ab-1</NUMBER></INDIVIDUAL_DOCUMENT></ENTITY>']) {
  parseCases.push({ in: xml, out: sanctions.parseUnList(xml) });
}
parseCases.push({ in: '<INDIVIDUAL><DATAID>1</DATAID><FIRST_NAME>&#x110000;</FIRST_NAME></INDIVIDUAL>', ...attempt(() => sanctions.parseUnList('<INDIVIDUAL><DATAID>1</DATAID><FIRST_NAME>&#x110000;</FIRST_NAME></INDIVIDUAL>')) });

const screenCases: unknown[] = [];
for (let i = 0; i < 800; i++) {
  const listIndex = r.int(0, lists.length - 1);
  const entries = lists[listIndex] as { name: string; aliases: string[]; documents: string[]; birthYears: string[]; kind: string }[];
  const source = entries.length > 0 && r.bool(0.7) ? r.pick(entries) : null;
  const words = source ? (r.bool(0.5) && source.aliases.length ? r.pick(source.aliases) : source.name).split(' ') : [];
  const mutate = (w: string) => (r.bool(0.2) && w.length > 4 ? w.slice(0, 2) + w.slice(3) : r.bool(0.1) ? w + 'a' : w);
  const name = source && r.bool(0.8) ? (r.bool(0.3) ? [...words].reverse() : words).map(mutate).join(' ') + (r.bool(0.2) ? ' Ltd' : '') : phrase(1, 4);
  const subject = {
    name,
    kind: source && r.bool(0.85) ? source.kind : r.pick(['individual', 'entity']),
    ...(r.bool(0.3) ? { aliases: [phrase(1, 3), ...(r.bool(0.3) ? [''] : [])] } : {}),
    ...(r.bool(0.4) ? { birthYear: source && source.birthYears.length && r.bool(0.6) ? r.pick(source.birthYears) : r.pick(['', null, '1971', '1980', String(r.int(1940, 2000))]) } : {}),
    ...(r.bool(0.3) ? { documents: source && source.documents.length && r.bool(0.5) ? [r.pick(source.documents).toLowerCase(), 'x1'] : [r.text([...'AB12 345-6/xy'], 0, 8)] } : {}),
  };
  const threshold = r.pick([undefined, undefined, undefined, 0.5, 0.75, 0.34, 1]);
  screenCases.push({ in: { subject, list: listIndex, threshold: threshold ?? null }, out: sanctions.screenSubject(subject, entries, threshold) });
}

// screenSubject's `list` is the index of a parseUnList case: its entries.
// ── suspension ──────────────────────────────────────────────────────────────────────────────────────────────────────
const easterCases: unknown[] = [];
const holidayCases: unknown[] = [];
for (let year = 1900; year <= 2200; year++) {
  easterCases.push({ in: year, out: suspension.orthodoxEaster(year) });
  if (year >= 1990 && year <= 2060) holidayCases.push({ in: year, out: [...suspension.georgianHolidays(year)].sort() });
}
const nonWorkingCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const d = i < 400 ? r.date('2026-01-01', '2026-12-31') : r.date('1995-01-01', '2045-12-31');
  nonWorkingCases.push({ in: d, out: suspension.isNonWorkingDay(d) });
}
const endCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const ms = Date.parse(`${r.date(i < 200 ? '1994-01-01' : '2020-01-01', i < 200 ? '2006-12-31' : '2035-12-31')}T00:00:00Z`) + r.int(0, 86_399_999) - (r.bool(0.3) ? 4 * 3_600_000 : 0);
  const startedAt = new Date(ms).toISOString();
  const hours = r.pick([undefined, undefined, 72, 24, 0, 1, 48, 100, 2400]);
  endCases.push({ in: { startedAt, hours: hours ?? null }, out: suspension.suspensionEnd(new Date(startedAt), hours).toISOString() });
}
for (const [startedAt, hours] of [['2026-10-05T06:00:00Z', 72], ['2026-10-09T10:00:00Z', 72], ['2026-10-14T08:00:00Z', 24], ['2026-12-31T20:00:00.000Z', 72], ['2026-04-09T19:59:59.999Z', 72]] as const) {
  endCases.push({ in: { startedAt: new Date(startedAt).toISOString(), hours }, out: suspension.suspensionEnd(new Date(startedAt), hours).toISOString() });
}
const deadlineCases: unknown[] = [];
for (let i = 0; i < 200; i++) {
  const at = new Date(Date.parse(`${r.date()}T00:00:00Z`) + r.int(0, 86_399_999)).toISOString();
  deadlineCases.push({ in: at, out: suspension.writtenConfirmationDeadline(new Date(at)).toISOString() });
}
const IDS = ['P1', 'P2', 'C1', 'C2', 'p1', ''];
const REFS = [undefined, null, '', ' ', 'CAS-1', 'cas-1 ', 'PAY-77', 'straße', 'STRASSE', '﻿CAS-1 ', 'CAS-1\u0085'];
const blockCases: unknown[] = [];
for (let i = 0; i < 1500; i++) {
  const s: Record<string, unknown> = {
    state: r.pick(['active', 'awaiting', 'unconfirmed', 'lapsed']),
    scope: r.pick(['all', 'transaction', 'transaction', 'transaction']),
    target_policy_id: r.bool(0.4) ? r.pick(IDS) : null,
    target_claim_id: r.bool(0.4) ? r.pick(IDS) : null,
  };
  if (r.bool(0.5)) s.legacy_unmapped = r.bool(0.7);
  if (r.bool(0.6)) s.transaction_ref = r.pick(REFS);
  const list = () => Array.from({ length: r.int(0, 3) }, () => r.pick([...IDS, null]));
  const t: Record<string, unknown> = {};
  if (r.bool(0.7)) t.policyIds = list();
  if (r.bool(0.6)) t.claimIds = list();
  if (r.bool(0.6)) t.numbers = Array.from({ length: r.int(0, 3) }, () => r.pick([...REFS, 'CAS-2']));
  blockCases.push({ in: { s, targets: t }, out: suspension.suspensionBlocks(s, t) });
}

const cases = {
  source: 'src/compliance/*.ts',
  riskPolicyFromSettings: settingsCases,
  assessClientRisk: assessCases,
  reviewDueFor: reviewCases,
  kycStatus: kycCases,
  transliterateGeorgian: translitCases,
  nameTokens: tokenCases,
  normalizeDocument: documentCases,
  parseUnList: parseCases,
  screenSubject: screenCases,
  orthodoxEaster: easterCases,
  georgianHolidays: holidayCases,
  isNonWorkingDay: nonWorkingCases,
  suspensionEnd: endCases,
  writtenConfirmationDeadline: deadlineCases,
  suspensionBlocks: blockCases,
};
const out = path.join(path.dirname(fileURLToPath(import.meta.url)), '../cases/compliance.json');
fs.writeFileSync(out, JSON.stringify(cases) + '\n');
console.log(out, Object.fromEntries(Object.entries(cases).filter(([k]) => k !== 'source').map(([k, v]) => [k, (v as unknown[]).length])));
