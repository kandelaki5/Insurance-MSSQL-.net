// Parity cases of the generic Excel writer (src/lib/xlsx-layout.ts and the SheetJS calls of aml-report.ts, bordereau.ts and
// policy-import.ts buildTemplate): the old code run on deterministic rows, the files it wrote normalized into
// tests/parity/cases/regulatory-layout.json for tests/Insurance.Domain.Tests/Excel/ExcelParityTests.cs.
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/regulatory-layout.ts
process.env.TZ = 'UTC';
import { failure, legacy, normalize, prng, text, writeCases, type Rng } from './regulatory-common.ts';

const zip = await legacy('src/lib/regulatory/zip.ts');
const layout = await legacy('src/lib/xlsx-layout.ts');
const XLSX = await legacy('node_modules/xlsx/xlsx.mjs');
const { buildTemplate } = await legacy('src/lib/policy-import.ts');

/** A value as the case writes it down: its JavaScript type and, for a number, its bits. */
type Encoded = { t: 's'; v: string } | { t: 'n'; v: string } | { t: 'b'; v: boolean } | { t: 'null' } | { t: 'u' } | { t: 'd'; v: string };
const bits = (x: number) => Buffer.from(new Float64Array([x]).buffer).toString('hex');
const decode = (e: Encoded): unknown => (e.t === 's' ? e.v : e.t === 'n' ? Buffer.from(e.v, 'hex').readDoubleLE(0) : e.t === 'b' ? e.v : e.t === 'null' ? null : e.t === 'd' ? new Date(e.v) : undefined);

const STRINGS = ['period', '#', 'policy', 'name', 'ლლლლ', 'WWW', 'iiii', '2026-10-20', '₾ 1 234.50', 'a\nb', ' lead', 'trail ', '\ttab', 'x\u0001y', 'cr\rlf', 'A&B <c> "d" \'e\'', '😀 emoji', ' nbsp', '', ' ',
  'ძალიან გრძელი სათაური რომელიც სამ სტრიქონზე მეტს იკავებს და სვეტს აფართოებს', 'MMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMM', 'Ⴀ', '„quoted“', '–', 'ჱჺ'];
function value(rng: Rng): Encoded {
  const k = rng.int(0, 19);
  if (k < 7) return { t: 's', v: rng.chance(0.5) ? rng.pick(STRINGS) : text(rng, 10) };
  if (k < 13) {
    const x = rng.pick([0, -0, 1, -1, 1.5, 0.1 + 0.2, 1e21, 1e-7, 123456789012345680000, 2 ** 53 + 2, 46024, -1234.56, 9999999999999.99, 0.000001]);
    return { t: 'n', v: bits(rng.chance(0.4) ? x : Math.round(rng.next() * 10 ** rng.int(0, 15)) / 10 ** rng.int(0, 4) * (rng.chance(0.2) ? -1 : 1)) };
  }
  if (k < 14) return { t: 'b', v: rng.chance(0.5) };
  if (k < 16) return { t: 'null' };
  if (k < 18) return { t: 'u' };
  return { t: 'd', v: rng.pick(['2026-01-02T00:00:00.000Z', '2026-10-20T13:45:30.250Z', '1900-02-28T00:00:00.000Z', '1899-12-31T00:00:00.000Z', '2100-12-31T23:59:59.999Z']) };
}
function rowsOf(rng: Rng, n: number, width: number): Encoded[][] {
  return Array.from({ length: n }, () => (rng.chance(0.05) ? [] : Array.from({ length: rng.int(0, width) }, () => value(rng))));
}
const GOOD_NAMES = ['ფორმა', 'N1-4', 'NBG list', "O'Brien", 'Sheet', 'a'.repeat(31), 'Premium', 'Claims', ' sp ', '&<>', "'quoted'", 'Balance', 'N9'];
const BAD_NAMES = ['a'.repeat(32), 'bad/name', 'x[1]', 'q?', 'star*'];
const sheetName = (rng: Rng) => (rng.chance(0.08) ? rng.pick(BAD_NAMES) : rng.pick(GOOD_NAMES));

function layoutCases() {
  const rng = prng(2020_10_20);
  const cases: unknown[] = [];
  for (let n = 0; n < 40; n++) {
    const nSheets = rng.int(1, 3);
    const sheets = Array.from({ length: nSheets }, () => {
      const rows = rowsOf(rng, rng.int(0, n % 10 === 9 ? 400 : 25), rng.int(1, 12));
      return { name: sheetName(rng), rows, header: rng.int(0, Math.min(3, Math.max(0, rows.length - 1))) };
    });
    const c: any = { sheets };
    const decoded = sheets.map((s) => ({ ...s, rows: s.rows.map((r) => r.map(decode)) }));
    c.fits = decoded.map((s) => layout.columnFits(s.rows, s.header));
    try { c.workbook = normalize(zip, layout.readableWorkbook(decoded), null); } catch (e) { c.workbook = failure(e); }
    cases.push(c);
  }
  return cases;
}

function writerCases() {
  const rng = prng(5_2026);
  const cases: unknown[] = [];
  for (let n = 0; n < 30; n++) {
    const sheets: any[] = [];
    for (let k = 0; k < rng.int(1, 4); k++) {
      if (rng.chance(0.5)) sheets.push({ kind: 'aoa', name: rng.chance(0.15) ? null : sheetName(rng), rows: rowsOf(rng, rng.int(0, 20), rng.int(1, 8)) });
      else {
        const keys = ['N2', 'რისკი', 'segment', '2021', '10', '0', '-1', '01', 'policyNumber', 'თანხა ₾'];
        const records = Array.from({ length: rng.int(0, 12) }, () => Array.from({ length: rng.int(0, 7) }, () => [rng.pick(keys), value(rng)]));
        sheets.push({ kind: 'json', name: sheetName(rng), header: rng.chance(0.3) ? [rng.pick(keys), rng.pick(keys)] : null, records });
      }
    }
    const c: any = { sheets };
    try {
      const wb = XLSX.utils.book_new();
      for (const s of sheets) {
        const ws = s.kind === 'aoa' ? XLSX.utils.aoa_to_sheet(s.rows.map((r: Encoded[]) => r.map(decode)))
          : XLSX.utils.json_to_sheet(s.records.map((r: [string, Encoded][]) => Object.fromEntries(r.map(([k, v]) => [k, decode(v)]))), s.header ? { header: s.header } : undefined);
        XLSX.utils.book_append_sheet(wb, ws, s.name ?? undefined);
      }
      c.workbook = normalize(zip, XLSX.write(wb, { type: 'buffer', bookType: 'xlsx' }), null);
    } catch (e) { c.workbook = failure(e); }
    cases.push(c);
  }
  return cases;
}

function templateCases() {
  const sets = [[], [{ code: 'mtpl', is_mandatory: true }], [{ code: 'casco', is_mandatory: false }, { code: 'mtpl', is_mandatory: true }, { code: 'gap', is_mandatory: false }]];
  return sets.map((coverages) => ({ coverages, workbook: normalize(zip, buildTemplate(coverages), null) }));
}

function widthCases() {
  const rng = prng(12);
  return Array.from({ length: 200 }, () => { const s = rng.chance(0.3) ? rng.pick(STRINGS) : text(rng, 8); const bold = rng.chance(0.5); return [s, bold, layout.textWidth(s, bold)]; });
}

writeCases('regulatory-layout.json', { layout: layoutCases(), writer: writerCases(), templates: templateCases(), widths: widthCases() });
