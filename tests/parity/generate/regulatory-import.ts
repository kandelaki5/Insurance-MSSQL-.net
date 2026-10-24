// Parity cases of the policy import's reading of an upload (src/lib/policy-import.ts parseImport, SheetJS XLSX.read + sheet_to_json):
// CSV texts, .xlsx and .xls files made here with SheetJS, each read by the old code; and V8's own reading of date texts, which
// decides what a CSV field becomes. Written to tests/parity/cases/regulatory-import.json for ExcelImportParityTests.cs.
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/regulatory-import.ts
process.env.TZ = 'UTC';
import { failure, legacy, prng, text, writeCases, type Rng } from './regulatory-common.ts';

const { parseImport, BASE_COLUMNS } = await legacy('src/lib/policy-import.ts');
const XLSX = await legacy('node_modules/xlsx/xlsx.mjs');

const FIELDS = ['Toyota', 'Corolla', '2021', '35000', '35,000', '35000.50', '1.50', '$1,200', '50%', '(7)', '-0', '0012', '0x1A', '1e3', 'Infinity', 'AA-001-AA', '1HGCM82633A004352',
  '12345678901234567', 'car', 'left', 'commercial', 'GEL', 'USD', '2026-01-15', '2026-1-5', '15.01.2026', '01.15.2026', '1/15/2026', '15/01/2026', '2026/01/15', 'Jan 15 2026',
  '15 Jan 2026', 'January 15, 2026', '2026-01-15T10:00', '2026-01-15 24:00', '2026-02-30', '5/6', '12', 'Toyota 2', 'ქონება 5', 'MTPL 2026', 'TRUE', 'FALSE', 'true', '=1+1', '="text"',
  '=', '  ', '', 'ა', 'ტოიოტა', 'კოროლა', '"quoted, with comma"', '"multi\nline"', '"a ""b"" c"', 'tab\there', '1 000', '2026', '2001-01-01', 'Jan', 'may', '12:00 PM 1/1/2026', '3 May',
  '0.1', '.5', '5.', '+7', '1,2,3', '9007199254740993', ' 12 ', '١٢', 'x 5', 'ab-12'];
const HEADERS = [...BASE_COLUMNS, 'premium_mtpl', 'limit_mtpl', 'deductible_mtpl', 'premium_casco', 'notes', '', 'make', 'year '];
const COVERAGES = [[], [{ code: 'mtpl', is_mandatory: true }], [{ code: 'mtpl', is_mandatory: true }, { code: 'casco', is_mandatory: false }]];
const DEFAULTS = [{}, { effective_from: '2026-11-01', effective_to: '2027-11-01', currency: 'GEL', installments: '1' }, { currency: 'USD', extra_key: 'x' }];

function field(rng: Rng): string {
  if (rng.chance(0.7)) return rng.pick(FIELDS);
  if (rng.chance(0.5)) return String(Math.round(rng.next() * 10 ** rng.int(0, 8)) / 10 ** rng.int(0, 3));
  return text(rng, 3).replace(/\n/g, ' ');
}
function csvCase(rng: Rng) {
  const sep = rng.pick([',', ',', ';', '\t', '|']);
  const nl = rng.pick(['\n', '\r\n', '\r']);
  const headers = Array.from({ length: rng.int(1, 14) }, () => rng.pick(HEADERS));
  const quote = (s: string) => (s.startsWith('"') ? s : s.includes(sep) || s.includes('\n') ? `"${s.replace(/"/g, '""')}"` : s);
  const lines = [headers.map(quote).join(sep)];
  for (let r = 0; r < rng.int(0, 12); r++) lines.push(rng.chance(0.08) ? '' : Array.from({ length: rng.int(0, headers.length + 1) }, () => quote(field(rng))).join(sep));
  let body = lines.join(nl) + (rng.chance(0.5) ? nl : '');
  if (rng.chance(0.1)) body = `sep=${sep}${nl}${body}`;
  const bytes = Buffer.concat([rng.chance(0.15) ? Buffer.from([0xef, 0xbb, 0xbf]) : Buffer.alloc(0), Buffer.from(body, 'utf8')]);
  return bytes;
}
function cellValue(rng: Rng, bookType: 'xlsx' | 'xls'): unknown {
  const k = rng.int(0, 9);
  if (k < 4) return field(rng);
  if (k < 6) return rng.pick([2021, 35000, 35000.5, 0.1 + 0.2, -0, 1e21, 46024, 12345678901234567, 0, 0.5, 1, 59, 61, -1, -2.5]);
  if (k < 7) return rng.chance(0.5);
  if (k < 8) return new Date(Date.UTC(rng.int(1899, 2100), rng.int(0, 11), rng.int(1, 31), rng.chance(0.3) ? rng.int(0, 23) : 0, rng.chance(0.3) ? 30 : 0));
  if (k < 9) return null;
  return undefined;
}
function bookCase(rng: Rng, bookType: 'xlsx' | 'xls') {
  // .xls: the first header present (the .xls reader takes the sheet's extent from its cells, not from the file's DIMENSIONS record).
  const headers = Array.from({ length: rng.int(1, 14) }, (_, i) => (rng.chance(0.05) && !(bookType === 'xls' && i === 0) ? rng.pick([2021, true, null]) : rng.pick(HEADERS)));
  const rows: unknown[][] = [headers];
  for (let r = 0; r < rng.int(0, 10); r++) rows.push(Array.from({ length: rng.int(0, headers.length + 1) }, () => cellValue(rng, bookType)));
  const ws = XLSX.utils.aoa_to_sheet(rows);
  // Some numbers in other formats: a custom date, a two-place amount, a time; one cell a formula with its cached value.
  for (const ref of Object.keys(ws).filter((k) => !k.startsWith('!'))) {
    const c = ws[ref];
    if (c.t === 'n' && rng.chance(0.2)) c.z = rng.pick(['yyyy-mm-dd', '0.00', 'dd.mm.yyyy', 'h:mm', '[h]:mm', '#,##0', '"₾"#,##0.00', 'General']);
    if (rng.chance(0.03)) c.f = 'A1&"x"';
  }
  if (rng.chance(0.1)) ws['!ref'] = 'A1:Z40';
  const wb = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(wb, ws, rng.pick(['policies', 'ფურცელი', 'Sheet1']));
  if (rng.chance(0.2)) XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet([['make'], ['second sheet']]), 'second');
  return XLSX.write(wb, { type: 'buffer', bookType, bookSST: rng.chance(0.5), compression: rng.chance(0.5) }) as Buffer;
}

const rowsView = (rows: any[]) => rows.map((r) => [r.row, Object.entries(r.values)]);

function importCases() {
  const rng = prng(220);
  const cases: unknown[] = [];
  const kinds = [...Array(90).fill('csv'), ...Array(50).fill('xlsx'), ...Array(25).fill('xls'), 'junk', 'junk', 'emptycsv', 'pkjunk'];
  for (const kind of kinds) {
    const bytes = kind === 'csv' ? csvCase(rng) : kind === 'xlsx' || kind === 'xls' ? bookCase(rng, kind)
      : kind === 'junk' ? Buffer.from(Array.from({ length: rng.int(1, 200) }, () => rng.int(0, 255))) : kind === 'emptycsv' ? Buffer.alloc(0) : Buffer.from('PK\u0003\u0004 not a zip');
    const coverages = rng.pick(COVERAGES), defaults = rng.pick(DEFAULTS);
    const c: any = { kind, bytes: bytes.toString('base64'), coverages, defaults };
    try {
      const ab = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
      c.rows = rowsView(parseImport(ab, coverages, defaults));
    } catch (e) { c.rows = failure(e); }
    cases.push(c);
  }
  return cases;
}

/** V8's reading of a date text (new Date(text)) in a UTC process: its time value, or null when invalid. */
function dateCases() {
  const rng = prng(15);
  const more = Array.from({ length: 300 }, () => {
    const parts = [String(rng.int(0, 3000)), rng.pick(['/', '-', '.', ' ', ':', ',', ', ', 'T', '']), String(rng.int(0, 40)), rng.pick(['/', '-', '.', ' ', '']), String(rng.int(0, 2100))];
    if (rng.chance(0.3)) parts.push(rng.pick([' 10:00', ' 10:00:30', ' 10:00 PM', ' 25:00', 'Z', ' GMT+0400', ' UTC', ' (comment)', ' pm', '+04:00', ' EST', ' 12:30:15.123456']));
    if (rng.chance(0.2)) parts.unshift(rng.pick(['Mon ', 'January ', 'jan ', 'ქონება ', 'x', '(', 'Sept ', 'decemberx ']));
    return parts.join('');
  });
  const inputs = [...FIELDS, ...more, '', ' ', '+002026-01-15', '-000000-01-01', '-002026-01-15', '2026-01-15T10:00:00.5Z', '2026-01-15T10:00:00+0400', '2026-01-15T10:00:00-04:00', '2026-01-15T24:00:01',
    'Tue, 20 Oct 2026 10:00:00 GMT', 'October 20, 2026 10:00:00 AM', '20 October 2026', '2026 October 20', 'Oct-20-2026', '20-Oct-2026', '1/2/3/4', '12/31/1969 23:59:59.999', '275760-09-13', '+275760-09-13T00:00:00.000Z', '+275760-09-13T00:00:00.001Z'];
  return inputs.map((s) => { const t = new Date(s).getTime(); return [s, Number.isNaN(t) ? null : t]; });
}

writeCases('regulatory-import.json', { cases: importCases(), dates: dateCases() });
