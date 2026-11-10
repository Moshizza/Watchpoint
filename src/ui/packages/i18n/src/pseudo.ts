export const PSEUDO_LOCALES = ['en-XA', 'ar-XB'] as const;
export type PseudoLocale = (typeof PSEUDO_LOCALES)[number];

const LATIN_TO_ACCENTED: Record<string, string> = {
  a: 'á',
  b: 'ḅ',
  c: 'ć',
  d: 'ḋ',
  e: 'é',
  f: 'ƒ',
  g: 'ǵ',
  h: 'ḣ',
  i: 'í',
  j: 'ǰ',
  k: 'ḱ',
  l: 'ĺ',
  m: 'ṁ',
  n: 'ń',
  o: 'ó',
  p: 'ṗ',
  q: 'ɋ',
  r: 'ŕ',
  s: 'ś',
  t: 'ṫ',
  u: 'ú',
  v: 'ṽ',
  w: 'ẃ',
  x: 'ẋ',
  y: 'ý',
  z: 'ź',
  A: 'Á',
  B: 'Ḅ',
  C: 'Ć',
  D: 'Ḋ',
  E: 'É',
  F: 'Ƒ',
  G: 'Ǵ',
  H: 'Ḣ',
  I: 'Í',
  J: 'ǰ',
  K: 'Ḱ',
  L: 'Ĺ',
  M: 'Ṁ',
  N: 'Ń',
  O: 'Ó',
  P: 'Ṗ',
  Q: 'Ɋ',
  R: 'Ŕ',
  S: 'Ś',
  T: 'Ṫ',
  U: 'Ú',
  V: 'Ṽ',
  W: 'Ẃ',
  X: 'Ẋ',
  Y: 'Ý',
  Z: 'Ź',
};

export function pseudoEnXA(text: string): string {
  let result = '';
  let inBrace = 0;
  for (let i = 0; i < text.length; i++) {
    const char = text[i] ?? '';
    if (char === '{') {
      inBrace++;
      result += char;
    } else if (char === '}') {
      inBrace = Math.max(0, inBrace - 1);
      result += char;
    } else if (inBrace > 0) {
      result += char;
    } else {
      result += LATIN_TO_ACCENTED[char] ?? char;
    }
  }
  const padLen = Math.ceil(text.length * 0.4);
  const pad = ' ' + '~'.repeat(Math.max(1, padLen));
  return `[${result}${pad}]`;
}

export function pseudoArXB(text: string): string {
  return `⟦${text}⟧`;
}
