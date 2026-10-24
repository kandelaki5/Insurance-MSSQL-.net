// Parity cases for the interface texts: every text, number and dictionary of src/i18n/*.ts as the old program has it,
// and the output of every text function on sample arguments. The C# test (Texts/TextsParityTests) finds each by its
// C# name and requires the same characters — and that the C# classes hold nothing the old files do not.
//
//   LEGACY_DIR=/path/to/Insurance-Software node tests/parity/generate/common-texts.ts
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildModel, type Node } from './common-texts-model.ts';

const SAMPLES: Record<string, unknown[]> = {
  string: ['', 'გიორგი ბერიძე', '14/10/2026', 'A{0}"\\$', ' x '],
  number: [0, 1, 7, -3, 1000000],
  'string[]': [[], ['05.1'], ['05.1', '06.2', '09']],
};

const product = (lists: unknown[][]): unknown[][] =>
  lists.reduce<unknown[][]>((acc, list) => acc.flatMap((prefix) => list.map((v) => [...prefix, v])), [[]]);

const items: unknown[] = [];
function walk(node: Node, cs: string) {
  switch (node.kind) {
    case 'class':
      for (const m of node.members) walk(m, `${cs}.${m.csName}`);
      if (node.entries) items.push({ kind: 'map', ts: node.tsPath, cs: `${cs}.ByKey`, entries: node.entries });
      break;
    case 'text': case 'number':
      items.push({ kind: node.kind, ts: node.tsPath, cs, value: node.value });
      break;
    case 'map': case 'map2':
      items.push({ kind: node.kind, ts: node.tsPath, cs, entries: node.entries });
      break;
    case 'function': {
      const calls = product(node.params.map((p) => SAMPLES[p.type]!)).map((args) => ({ args, result: node.fn(...args) }));
      items.push({ kind: 'function', ts: node.tsPath, cs, params: node.params.map((p) => p.type), calls });
      break;
    }
  }
}

const model = await buildModel();
for (const f of model) walk(f.root, f.root.csName);

const out = path.join(path.dirname(fileURLToPath(import.meta.url)), '../cases/common-texts.json');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify({ source: 'src/i18n/*.ts', classes: model.map((f) => f.root.csName), items }, null, 1) + '\n');
const count = (k: string) => items.filter((i) => (i as { kind: string }).kind === k).length;
console.log(`${out}: ${items.length} items (texts ${count('text')}, numbers ${count('number')}, maps ${count('map')}, map2 ${count('map2')}, functions ${count('function')})`);
