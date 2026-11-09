import { describe, expect, it } from 'vitest';
import { darkTheme, highContrastTheme, lightTheme, type ThemeVariables } from './index.ts';

function hexToRgb(hex: string): [number, number, number] {
  let c = hex.replace('#', '');
  if (c.length === 3) {
    c = c
      .split('')
      .map((x) => x + x)
      .join('');
  }
  const num = parseInt(c, 16);
  return [(num >> 16) & 255, (num >> 8) & 255, num & 255];
}

function sRgbToLin(c: number): number {
  c = c / 255;
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function getLuminance(hex: string): number {
  const [r, g, b] = hexToRgb(hex);
  return 0.2126 * sRgbToLin(r) + 0.7152 * sRgbToLin(g) + 0.0722 * sRgbToLin(b);
}

function getContrast(hex1: string, hex2: string): number {
  const lum1 = getLuminance(hex1);
  const lum2 = getLuminance(hex2);
  const brightest = Math.max(lum1, lum2);
  const darkest = Math.min(lum1, lum2);
  return (brightest + 0.05) / (darkest + 0.05);
}

const STATE_NAMES = [
  'alarm',
  'fault',
  'warning',
  'normal-active',
  'normal',
  'maintenance',
  'disabled',
  'offline',
  'unknown',
] as const;

describe('UI tokens test', () => {
  it('UIC-U-042 state tokens contrast >= 4.5:1 in all themes (computed)', () => {
    const themeList: { name: string; theme: ThemeVariables }[] = [
      { name: 'dark', theme: darkTheme },
      { name: 'light', theme: lightTheme },
      { name: 'high-contrast', theme: highContrastTheme },
    ];

    for (const { name, theme } of themeList) {
      for (const state of STATE_NAMES) {
        const bgKey = `--wp-state-${state}-bg` as keyof ThemeVariables;
        const fgKey = `--wp-state-${state}-fg` as keyof ThemeVariables;
        const bg = theme[bgKey];
        const fg = theme[fgKey];
        const ratio = getContrast(bg, fg);

        expect(
          ratio,
          `Theme "${name}" state "${state}" (bg: ${bg}, fg: ${fg}) has contrast ${ratio.toFixed(2)}:1, expected >= 4.5:1`,
        ).toBeGreaterThanOrEqual(4.5);
      }
    }
  });
});
