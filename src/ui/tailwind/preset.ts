import type { Config } from 'tailwindcss';

/**
 * Semantic colours of the design system (spec UIC §9.1). Each maps to a CSS variable defined by the themes
 * (light, dark — the default for control rooms — and high contrast) in @watchpoint/ui, so themes switch at
 * run time without rebuilding. The variable values arrive with the design system (T1.22).
 */
export const colorTokens = {
  bg: 'var(--wp-bg)',
  surface: 'var(--wp-surface)',
  'surface-raised': 'var(--wp-surface-raised)',
  border: 'var(--wp-border)',
  text: 'var(--wp-text)',
  'text-muted': 'var(--wp-text-muted)',
  'text-inverse': 'var(--wp-text-inverse)',
} as const;

/** Overall device states (spec SCM §3.1); each has a base, a background and a foreground variable. */
export const stateNames = [
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

/** Alarm priority bands (M2). */
export const priorityNames = ['critical', 'high', 'medium', 'low', 'info'] as const;

const stateColors = Object.fromEntries(
  stateNames.map((state) => [
    state,
    {
      DEFAULT: `var(--wp-state-${state})`,
      bg: `var(--wp-state-${state}-bg)`,
      fg: `var(--wp-state-${state}-fg)`,
    },
  ]),
);

const priorityColors = Object.fromEntries(priorityNames.map((band) => [band, `var(--wp-prio-${band})`]));

/**
 * Tailwind preset shared by every UI package and app (use it with `@config` in the app stylesheet).
 * Classes: `bg-wp-surface`, `text-wp-text-muted`, `bg-wp-state-alarm-bg`, `text-wp-prio-critical`…
 * Direction utilities must be logical (`ms-`, `pe-`, `start-`, `text-end`…); the lint rule
 * `wp/no-physical-direction-classes` enforces it, as Tailwind 4 cannot disable core utilities.
 */
export const preset = {
  theme: {
    extend: {
      colors: {
        wp: {
          ...colorTokens,
          state: stateColors,
          prio: priorityColors,
        },
      },
      fontFamily: {
        sans: ['"Noto Sans"', '"Noto Sans Arabic"', 'system-ui', 'sans-serif'],
      },
    },
  },
} satisfies Partial<Config>;

export default preset;
