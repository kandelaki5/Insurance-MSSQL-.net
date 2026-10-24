// One-off migration tool: wrote src/Insurance.Application/Texts/*.cs from the old src/i18n/*.ts (see common-texts-model.ts
// for the shape). Kept for provenance; the C# files are the source of the texts from now on, and common-texts.ts proves
// they still say exactly what the old program said.
//
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/common-texts-csharp.ts src/Insurance.Application/Texts
import fs from 'node:fs';
import path from 'node:path';
import { buildModel, ts, type Node } from './common-texts-model.ts';

const OUT = process.argv[2];
if (!OUT) throw new Error('usage: common-texts-csharp.ts <output folder>');

const INV = 'global::System.Globalization.CultureInfo.InvariantCulture';
const MAP = 'global::Insurance.Application.Texts.TextMap.Of';
const DICT = 'global::System.Collections.Generic.IReadOnlyDictionary';

/** A C# string literal with the same characters; invisible and line-breaking characters are written as escapes. */
function lit(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const ch = s[i]!;
    const c = ch.charCodeAt(0);
    if (ch === '\\') out += '\\\\';
    else if (ch === '"') out += '\\"';
    else if (ch === '\n') out += '\\n';
    else if (ch === '\r') out += '\\r';
    else if (ch === '\t') out += '\\t';
    else if (c < 0x20 || (c >= 0x7f && c <= 0x9f) || c === 0xa0 || c === 0xad || (c >= 0x200b && c <= 0x200f) || c === 0x2028 || c === 0x2029 || c === 0x202f || c === 0xfeff || (c >= 0xd800 && c <= 0xdfff)) {
      out += '\\u' + c.toString(16).toUpperCase().padStart(4, '0');
    } else out += ch;
  }
  return out + '"';
}

const xml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function comments(raw: string[], indent: string): string[] {
  const lines: string[] = [];
  for (const c of raw) {
    if (c.startsWith('/**')) {
      const body = c.slice(3, -2).split('\n').map((l) => l.replace(/^\s*\* ?/, '').trimEnd());
      while (body.length && body[0]!.trim() === '') body.shift();
      while (body.length && body[body.length - 1]!.trim() === '') body.pop();
      lines.push(`${indent}/// <summary>`);
      for (const l of body) lines.push(`${indent}/// ${xml(l.trim())}`.trimEnd());
      lines.push(`${indent}/// </summary>`);
    } else if (c.startsWith('//')) {
      lines.push(`${indent}${c.trimEnd()}`);
    } else {
      throw new Error(`block comment: ${c}`);
    }
  }
  return lines;
}

const csType = (t: string) => {
  if (t === 'string') return 'string';
  if (t === 'number') return 'int';
  if (t === 'string[]') return 'global::System.Collections.Generic.IReadOnlyList<string>';
  throw new Error(`parameter type ${t}`);
};

/** Functions whose body is more than a template of their parameters, written by hand. */
const MANUAL: Record<string, (p: string[]) => string> = {
  'policyTexts.import.previewSummary': (p) =>
    `string.Concat("სტრიქონი სულ: ", ${p[0]}.ToString(${INV}), ", სწორი: ", ${p[1]}.ToString(${INV}), ", შეცდომით: ", (${p[0]} - ${p[1]}).ToString(${INV}))`,
  'regulatoryTexts.form6Overflow': (p) =>
    `string.Concat("შაბლონის ბადეში ექვსი სახეობისთვის ხუთ-ხუთი სტრიქონია: ", ${p[0]}.ToString(${INV}), " სტრიქონი ბადის ქვემოთ, დამატებით სტრიქონებშია ჩაწერილი", ${p[1]}.Count > 0 ? string.Concat(" (სახეობები: ", string.Join(", ", ${p[1]}), ")") : "", ". ყველა სტრიქონი ფაილშიც და გვერდზეც არის.")`,
};

function functionBody(node: Extract<Node, { kind: 'function' }>): string {
  const names = node.params.map((p) => p.name);
  if (MANUAL[node.tsPath]) return MANUAL[node.tsPath]!(names);
  const body = (node.node as any).body;
  if (!ts.isTemplateExpression(body)) throw new Error(`${node.tsPath} needs a hand-written body`);
  const parts: string[] = [];
  if (body.head.text) parts.push(lit(body.head.text));
  for (const span of body.templateSpans) {
    if (!ts.isIdentifier(span.expression)) throw new Error(`${node.tsPath} needs a hand-written body`);
    const p = node.params.find((x) => x.name === span.expression.text)!;
    parts.push(p.type === 'string' ? p.name : `${p.name}.ToString(${INV})`);
    if (span.literal.text) parts.push(lit(span.literal.text));
  }
  return `string.Concat(${parts.join(', ')})`;
}

const mapOf = (entries: [string, string][], indent: string) =>
  `${MAP}(\n${entries.map(([k, v]) => `${indent}    (${lit(k)}, ${lit(v)})`).join(',\n')})`;

function emitMembers(members: Node[], entries: [string, string][] | null, indent: string, out: string[]) {
  const blocks: string[][] = [];
  for (const m of members) {
    const b = comments(m.comments, indent);
    switch (m.kind) {
      case 'text': b.push(`${indent}public const string ${m.csName} = ${lit(m.value)};`); break;
      case 'number': b.push(`${indent}public const int ${m.csName} = ${m.value};`); break;
      case 'map': b.push(`${indent}public static readonly ${DICT}<string, string> ${m.csName} = ${mapOf(m.entries, indent)};`); break;
      case 'map2':
        b.push(`${indent}public static readonly ${DICT}<string, ${DICT}<string, string>> ${m.csName} = ${MAP}(\n${m.entries
          .map(([k, v]) => `${indent}    (${lit(k)}, ${mapOf(v, indent + '    ')})`).join(',\n')});`);
        break;
      case 'function':
        b.push(`${indent}public static string ${m.csName}(${m.params.map((p) => `${csType(p.type)} ${p.name}`).join(', ')}) =>`);
        b.push(`${indent}    ${functionBody(m)};`);
        break;
      case 'class':
        b.push(`${indent}public static class ${m.csName}`, `${indent}{`);
        emitMembers(m.members, m.entries, indent + '    ', b);
        b.push(`${indent}}`);
        break;
    }
    blocks.push(b);
  }
  if (entries) {
    blocks.push([
      `${indent}/// <summary>The texts above by their old keys, in their order, for a lookup by code.</summary>`,
      `${indent}public static readonly ${DICT}<string, string> ByKey = ${MAP}(\n${entries.map(([k]) => {
        const member = members.find((m) => m.kind === 'text' && m.tsPath.slice(m.tsPath.lastIndexOf('.') + 1) === k);
        if (!member) throw new Error(`ByKey: no text ${k}`);
        return `${indent}    (${lit(k)}, ${member.csName})`;
      }).join(',\n')});`,
    ]);
  }
  const tall = (b: string[]) => b.length > 1 || b.some((l) => l.includes('\n'));
  blocks.forEach((b, i) => { if (i > 0 && (tall(b) || tall(blocks[i - 1]!))) out.push(''); out.push(...b); });
}

for (const f of await buildModel()) {
  const out: string[] = [];
  out.push(...comments(f.headerComments, ''));
  out.push(`// Ported from src/i18n/${f.file} (${f.exportName}); every text is byte-for-byte the old one.`);
  out.push('namespace Insurance.Application.Texts;', '', `public static class ${f.root.csName}`, '{');
  emitMembers(f.root.members, null, '    ', out);
  out.push('}');
  fs.writeFileSync(path.join(OUT, `${f.root.csName}.cs`), out.join('\n') + '\n');
  console.log(`${f.file} -> ${f.root.csName}.cs`);
}
