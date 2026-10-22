// The shape of the old interface texts (src/i18n/*.ts) as the C# port mirrors it, shared by
// common-texts-csharp.ts (which wrote src/Insurance.Application/Texts/*.cs) and common-texts.ts (the parity cases).
//
// Structure and comments come from the TypeScript syntax tree; every value comes from the module as Node evaluates it.
// Rules of the C# shape:
// - one static class per file, named after the export (ka → Ka, policyTexts → PolicyTexts);
// - a nested object becomes a nested static class, its keys PascalCase (a member named like its class gets "Text");
// - an object typed `as Record<…>` or with a key that is not an identifier becomes an ordered read-only dictionary;
// - a nested class whose values are all texts also gets `ByKey`, the same texts as a dictionary by their old keys;
// - a function becomes a static method with the same output.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

export const LEGACY = process.env.LEGACY_DIR ?? '/home/user/legacy-run';
const require = createRequire(path.join(LEGACY, 'package.json'));
export const ts = require('typescript');

export type Node =
  | { kind: 'class'; tsPath: string; csName: string; comments: string[]; members: Node[]; entries: [string, string][] | null }
  | { kind: 'text'; tsPath: string; csName: string; comments: string[]; value: string }
  | { kind: 'number'; tsPath: string; csName: string; comments: string[]; value: number }
  | { kind: 'map'; tsPath: string; csName: string; comments: string[]; entries: [string, string][] }
  | { kind: 'map2'; tsPath: string; csName: string; comments: string[]; entries: [string, [string, string][]][] }
  | { kind: 'function'; tsPath: string; csName: string; comments: string[]; fn: (...args: unknown[]) => string; params: { name: string; type: string }[]; node: unknown };

export interface FileModel {
  file: string;
  exportName: string;
  root: Extract<Node, { kind: 'class' }>;
  headerComments: string[];
}

const IDENT = /^[A-Za-z_][A-Za-z0-9_]*$/;
const pascal = (key: string) => key[0]!.toUpperCase() + key.slice(1);

function rawComments(text: string, node: { getFullStart(): number }): string[] {
  return (ts.getLeadingCommentRanges(text, node.getFullStart()) ?? []).map((r: { pos: number; end: number }) => text.slice(r.pos, r.end));
}

function unwrap(expr: any): { expr: any; record: 0 | 1 | 2 } {
  let record: 0 | 1 | 2 = 0;
  for (;;) {
    if (ts.isParenthesizedExpression(expr) || ts.isSatisfiesExpression(expr)) { expr = expr.expression; continue; }
    if (ts.isAsExpression(expr)) {
      const t = expr.type.getText();
      if (t === 'Record<string, string>') record = 1;
      else if (t === 'Record<string, Record<string, string>>') record = 2;
      else if (t !== 'const') throw new Error(`unknown type assertion ${t}`);
      expr = expr.expression;
      continue;
    }
    return { expr, record };
  }
}

const allTexts = (value: object) => Object.values(value).every((v) => typeof v === 'string');

function buildClass(text: string, obj: any, runtime: Record<string, unknown>, csName: string, tsPath: string, comments: string[]): Extract<Node, { kind: 'class' }> {
  const members: Node[] = [];
  const names = new Set<string>();
  for (const prop of obj.properties) {
    if (!ts.isPropertyAssignment(prop)) throw new Error(`${tsPath}: ${ts.SyntaxKind[prop.kind]}`);
    const key: string = ts.isIdentifier(prop.name) || ts.isStringLiteral(prop.name) || ts.isNumericLiteral(prop.name) ? prop.name.text : prop.name.getText();
    if (!IDENT.test(key)) throw new Error(`${tsPath}: key ${key} is not an identifier`);
    let name = pascal(key);
    if (name === csName) name += 'Text';
    if (name === 'ByKey') throw new Error(`${tsPath}.${key}: collides with ByKey`);
    if (names.has(name)) throw new Error(`${tsPath}.${key}: duplicate ${name}`);
    names.add(name);
    const value = runtime[key];
    const { expr, record } = unwrap(prop.initializer);
    const p = `${tsPath}.${key}`;
    const c = rawComments(text, prop);
    const isObject = value !== null && typeof value === 'object' && !Array.isArray(value);
    const keysAreNames = isObject && Object.keys(value as object).every((k) => IDENT.test(k));
    if (record === 2) {
      members.push({ kind: 'map2', tsPath: p, csName: name, comments: c, entries: Object.entries(value as Record<string, Record<string, string>>).map(([k, v]) => [k, Object.entries(v)]) });
    } else if (record === 1 || (isObject && !keysAreNames)) {
      if (!allTexts(value as object)) throw new Error(`${p}: a dictionary of something other than texts`);
      members.push({ kind: 'map', tsPath: p, csName: name, comments: c, entries: Object.entries(value as Record<string, string>) });
    } else if (typeof value === 'string') {
      members.push({ kind: 'text', tsPath: p, csName: name, comments: c, value });
    } else if (typeof value === 'number' && Number.isInteger(value)) {
      members.push({ kind: 'number', tsPath: p, csName: name, comments: c, value });
    } else if (typeof value === 'function') {
      if (!ts.isArrowFunction(expr)) throw new Error(`${p}: not an arrow function`);
      const params = expr.parameters.map((x: any) => ({ name: x.name.getText(), type: x.type.getText() }));
      members.push({ kind: 'function', tsPath: p, csName: name, comments: c, fn: value as (...args: unknown[]) => string, params, node: expr });
    } else if (isObject) {
      if (!ts.isObjectLiteralExpression(expr)) throw new Error(`${p}: not an object literal`);
      members.push(buildClass(text, expr, value as Record<string, unknown>, name, p, c));
    } else {
      throw new Error(`${p}: unsupported value ${typeof value}`);
    }
  }
  const entries = allTexts(runtime) ? Object.entries(runtime as Record<string, string>) : null;
  return { kind: 'class', tsPath, csName, comments, members, entries };
}

export async function buildModel(): Promise<FileModel[]> {
  const dir = path.join(LEGACY, 'src/i18n');
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.ts')).sort();
  const result: FileModel[] = [];
  for (const file of files) {
    const full = path.join(dir, file);
    const text = fs.readFileSync(full, 'utf8');
    const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
    const mod = await import(pathToFileURL(full).href);
    const statements = sf.statements.filter((s: any) => ts.isVariableStatement(s));
    if (statements.length !== 1 || Object.keys(mod).length !== 1) throw new Error(`${file}: expected one exported object`);
    const decl = statements[0].declarationList.declarations[0];
    const exportName: string = decl.name.getText();
    const { expr } = unwrap(decl.initializer);
    // The top class never gets Entries: a file's texts are reached by name.
    const root = buildClass(text, expr, mod[exportName], pascal(exportName), exportName, []);
    result.push({ file, exportName, root: { ...root, entries: null }, headerComments: rawComments(text, statements[0]) });
  }
  return result;
}
