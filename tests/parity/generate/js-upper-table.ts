// Writes src/Insurance.Domain/Common/JsUpper.txt: every code point whose JavaScript toUpperCase() is not itself, with
// its upper case, as the old program's runtime gives it. The C# JsText.ToUpperCase reads only this table, so its answer
// is the same on every operating system and .NET version (Codex R04). Regenerate with the old program's Node version
// (docs/RUNTIME.md) and regenerate cases/common.json's toUpperCase with the same Node:
//   node tests/parity/generate/js-upper-table.ts
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const lines = [`# JavaScript String.prototype.toUpperCase, Node ${process.version}, Unicode ${process.versions.unicode}, ICU ${process.versions.icu}`];
const upper: [number, string][] = [];
for (let cp = 0; cp <= 0x10ffff; cp++) {
  if (cp >= 0xd800 && cp <= 0xdfff) continue;
  const s = String.fromCodePoint(cp);
  const u = s.toUpperCase();
  if (u === s) continue;
  upper.push([cp, u]);
  lines.push(`${cp.toString(16).toUpperCase()} ${[...u].map((c) => c.codePointAt(0)!.toString(16).toUpperCase()).join(' ')}`);
}
fs.writeFileSync(path.join(here, '../../../src/Insurance.Domain/Common/JsUpper.txt'), lines.join('\n') + '\n');
// The same list in the parity cases.
const casesFile = path.join(here, '../cases/common.json');
const cases = JSON.parse(fs.readFileSync(casesFile, 'utf8'));
cases.toUpperCase = upper;
cases.toUpperCaseRuntime = `Node ${process.version}, Unicode ${process.versions.unicode}`;
fs.writeFileSync(casesFile, JSON.stringify(cases) + '\n');
console.log(lines[0], upper.length);
