// A small seeded random generator for the parity generators (mulberry32), so every run writes the same cases.
export function rng(seed: number) {
  let a = seed >>> 0;
  const next = () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const int = (min: number, max: number) => min + Math.floor(next() * (max - min + 1));
  const pick = <T>(items: readonly T[]): T => items[Math.floor(next() * items.length)]!;
  const bool = (p = 0.5) => next() < p;
  const date = (from = '2000-01-01', to = '2040-12-31') => {
    const a0 = Date.parse(`${from}T00:00:00Z`) / 86_400_000;
    const b0 = Date.parse(`${to}T00:00:00Z`) / 86_400_000;
    return new Date(int(a0, b0) * 86_400_000).toISOString().slice(0, 10);
  };
  const text = (alphabet: readonly string[], min: number, max: number) => {
    const n = int(min, max);
    let s = '';
    for (let i = 0; i < n; i++) s += pick(alphabet);
    return s;
  };
  return { next, int, pick, bool, date, text };
}

export const legacyDir = process.env.LEGACY_DIR ?? '/home/user/legacy-run';
export const legacy = async (relative: string) => import(new URL(`file://${legacyDir}/${relative}`).href);
