// Shared by the regulatory parity generators: the old code's location, a seeded PRNG, and the normalized view of a workbook that the
// C# tests compute the same way (tests/Insurance.Domain.Tests/Regulatory/ParityNormalizer.cs). Never compares ZIP bytes: each part's
// uncompressed content (its SHA-256), and for every changed sheet the cells, rows, merges and dimension that differ from the template.
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

export const LEGACY = process.env.LEGACY_DIR ?? '/home/user/legacy-run';
export const legacy = (path: string) => import(`file://${LEGACY}/${path}`);
export const CASES = new URL('../cases/', import.meta.url);

/** mulberry32: the same sequence for the same seed, every run. */
export function prng(seed: number) {
  let a = seed >>> 0;
  const next = () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  const int = (lo: number, hi: number) => lo + Math.floor(next() * (hi - lo + 1));
  const pick = <T>(xs: readonly T[]): T => xs[int(0, xs.length - 1)]!;
  const chance = (p: number) => next() < p;
  return { next, int, pick, chance };
}
export type Rng = ReturnType<typeof prng>;

export const sha = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');

const CELL = /<c\b([^>]*?)(?:\/>|>([\s\S]*?)<\/c>)/g;
const attr = (attrs: string, name: string) => new RegExp(`\\b${name}="([^"]*)"`).exec(attrs)?.[1] ?? null;

/** A sheet's XML as cells (ref → [s, t, f, v, is]), rows (r → attributes), merges and dimension. */
export function sheetView(xml: string) {
  const cells: [string, (string | null)[]][] = [];
  for (const m of xml.matchAll(CELL)) {
    const a = m[1]!, body = m[2] ?? '';
    const f = /<f\b[^>]*?(?:\/>|>[\s\S]*?<\/f>)/.exec(body)?.[0] ?? null;
    const v = /<v(?:\s[^>]*)?>([\s\S]*?)<\/v>/.exec(body)?.[1] ?? null;
    const is = /<is>([\s\S]*?)<\/is>/.exec(body)?.[1] ?? null;
    cells.push([attr(a, 'r') ?? '', [attr(a, 's'), attr(a, 't'), f, v, is]]);
  }
  const rows = [...xml.matchAll(/<row\b([^>]*?)(\/?)>/g)].map((m) => [attr(m[1]!, 'r') ?? '', m[1]!.trim()] as [string, string]);
  const merges = [...xml.matchAll(/<mergeCell\b[^>]*\bref="([^"]+)"/g)].map((m) => m[1]!);
  const dimension = /<dimension ref="([^"]*)"/.exec(xml)?.[1] ?? null;
  return { cells, rows, merges, dimension };
}

/**
 * The normalized view of a workbook against its template (null: a workbook written from nothing — every sheet is "changed"): each
 * part's name and SHA-256 of its uncompressed content; the workbook's sheets, defined names and calcPr; the styles' cell formats
 * beyond the template's; for each sheet whose XML changed, the cells and rows that differ from the template's (or all), its merges
 * and dimension, and the order of its cells.
 */
export function normalize(zip: { readZip: (b: Buffer) => any[]; entryData: (e: any) => Buffer }, buf: Buffer, template: Buffer | null) {
  const entries = zip.readZip(buf);
  const text = (es: any[], name: string) => { const e = es.find((x) => x.name === name); return e ? zip.entryData(e).toString('utf8') : null; };
  const before = template ? zip.readZip(template) : [];
  const parts = entries.map((e) => [e.name, sha(zip.entryData(e))]);
  const book = text(entries, 'xl/workbook.xml') ?? '';
  const rels = new Map([...(text(entries, 'xl/_rels/workbook.xml.rels') ?? '').matchAll(/<Relationship\b[^>]*\bId="([^"]+)"[^>]*\bTarget="([^"]+)"/g)].map((m) => [m[1]!, m[2]!]));
  const sheetTags = [...book.matchAll(/<sheet\b[^>]*\bname="([^"]*)"[^>]*\br:id="([^"]+)"/g)].map((m) => [m[1]!, m[2]!]);
  const definedNames = [...book.matchAll(/<definedName\b([^>]*)>([\s\S]*?)<\/definedName>/g)].map((m) => [m[1]!, m[2]!]);
  const calcPr = /<calcPr\b[^>]*\/>/.exec(book)?.[0] ?? null;
  const xfs = (x: string | null) => (x ? [...(/<cellXfs\b[^>]*>([\s\S]*?)<\/cellXfs>/.exec(x)?.[1] ?? '').matchAll(/<xf\b[^>]*?(?:\/>|>[\s\S]*?<\/xf>)/g)].map((m) => m[0]) : []);
  const xfNow = xfs(text(entries, 'xl/styles.xml')), xfWas = xfs(template ? text(before, 'xl/styles.xml') : null);
  const sheets = sheetTags.map(([name, rid]) => {
    const target = rels.get(rid!) ?? '';
    const path = target.startsWith('/') ? target.slice(1) : `xl/${target}`;
    const xml = text(entries, path) ?? '';
    const was = template ? text(before, path) : null;
    if (was !== null && was === xml) return { name, path, changed: false };
    const now = sheetView(xml), old = was === null ? null : sheetView(was);
    const oldCells = new Map(old?.cells ?? []), oldRows = new Map(old?.rows ?? []);
    return {
      name, path, changed: true,
      cells: now.cells.filter(([ref, c]) => JSON.stringify(oldCells.get(ref)) !== JSON.stringify(c)),
      removedCells: [...oldCells.keys()].filter((ref) => !now.cells.some(([r]) => r === ref)),
      cellOrder: sha(now.cells.map(([r]) => r).join(',')),
      rows: now.rows.filter(([r, a]) => oldRows.get(r) !== a),
      rowOrder: sha(now.rows.map(([r]) => r).join(',')),
      merges: now.merges, dimension: now.dimension,
    };
  });
  return { parts, sheets, definedNames, calcPr, addedXfs: xfNow.slice(xfWas.length), xfCount: xfNow.length };
}

/** An exception as the case records it: its message — or, for a crash of the old code itself (a TypeError: it read a property of
 * nothing), only that it failed; the C# code refuses the same input with its own message. */
export const failure = (e: unknown) => (e instanceof TypeError ? { crash: 'TypeError' } : { error: e instanceof Error ? e.message : String(e) });

export function writeCases(file: string, data: unknown) {
  const url = new URL(file, CASES);
  mkdirSync(dirname(url.pathname), { recursive: true });
  writeFileSync(url, JSON.stringify(data) + '\n');
  console.log(`${file}: ${(JSON.stringify(data).length / 1e6).toFixed(2)} MB`);
}

/** Texts that exercise what the forms do with text: Georgian, long, XML-special, $-patterns, white space of every kind. */
export const WORDS = ['გადამზღვეველი', 'სააქციო', 'საზოგადოება', 'ბროკერი', 'Munich', 'Re', 'Swiss', '(გამოგონილი)', 'A&B', '<test>', '"quoted"', "O'Brien", '$&', "$'", '$1', '$$', '_', '__', 'x'.repeat(40), 'ძალიან', 'გრძელი', 'დასახელების', '—', '=1+1', 'Ltd.', 'Insurance', 'Company', 'ს.ს.', '007'];
export function text(rng: Rng, maxWords: number) {
  const n = rng.int(1, maxWords);
  const seps = [' ', ' ', ' ', '  ', ' ', '\t', ' \n '];
  let s = '';
  for (let i = 0; i < n; i++) s += (i ? rng.pick(seps) : '') + rng.pick(WORDS);
  if (rng.chance(0.1)) s = ` ${s} `;
  return s;
}
