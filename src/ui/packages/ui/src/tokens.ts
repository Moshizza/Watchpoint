export interface ThemeVariables {
  '--wp-bg': string;
  '--wp-surface': string;
  '--wp-surface-raised': string;
  '--wp-border': string;
  '--wp-text': string;
  '--wp-text-muted': string;
  '--wp-text-inverse': string;

  '--wp-state-alarm': string;
  '--wp-state-alarm-bg': string;
  '--wp-state-alarm-fg': string;

  '--wp-state-fault': string;
  '--wp-state-fault-bg': string;
  '--wp-state-fault-fg': string;

  '--wp-state-warning': string;
  '--wp-state-warning-bg': string;
  '--wp-state-warning-fg': string;

  '--wp-state-normal-active': string;
  '--wp-state-normal-active-bg': string;
  '--wp-state-normal-active-fg': string;

  '--wp-state-normal': string;
  '--wp-state-normal-bg': string;
  '--wp-state-normal-fg': string;

  '--wp-state-maintenance': string;
  '--wp-state-maintenance-bg': string;
  '--wp-state-maintenance-fg': string;

  '--wp-state-disabled': string;
  '--wp-state-disabled-bg': string;
  '--wp-state-disabled-fg': string;

  '--wp-state-offline': string;
  '--wp-state-offline-bg': string;
  '--wp-state-offline-fg': string;

  '--wp-state-unknown': string;
  '--wp-state-unknown-bg': string;
  '--wp-state-unknown-fg': string;

  '--wp-prio-critical': string;
  '--wp-prio-high': string;
  '--wp-prio-medium': string;
  '--wp-prio-low': string;
  '--wp-prio-info': string;
}

export const darkTheme: ThemeVariables = {
  '--wp-bg': '#0f172a',
  '--wp-surface': '#1e293b',
  '--wp-surface-raised': '#334155',
  '--wp-border': '#475569',
  '--wp-text': '#f8fafc',
  '--wp-text-muted': '#94a3b8',
  '--wp-text-inverse': '#0f172a',

  '--wp-state-alarm': '#ef4444',
  '--wp-state-alarm-bg': '#450a0a',
  '--wp-state-alarm-fg': '#fecaca',

  '--wp-state-fault': '#f97316',
  '--wp-state-fault-bg': '#431407',
  '--wp-state-fault-fg': '#fed7aa',

  '--wp-state-warning': '#eab308',
  '--wp-state-warning-bg': '#422006',
  '--wp-state-warning-fg': '#fef08a',

  '--wp-state-normal-active': '#10b981',
  '--wp-state-normal-active-bg': '#022c22',
  '--wp-state-normal-active-fg': '#a7f3d0',

  '--wp-state-normal': '#22c55e',
  '--wp-state-normal-bg': '#052e16',
  '--wp-state-normal-fg': '#bbf7d0',

  '--wp-state-maintenance': '#06b6d4',
  '--wp-state-maintenance-bg': '#083344',
  '--wp-state-maintenance-fg': '#a5f3fc',

  '--wp-state-disabled': '#64748b',
  '--wp-state-disabled-bg': '#1e293b',
  '--wp-state-disabled-fg': '#cbd5e1',

  '--wp-state-offline': '#475569',
  '--wp-state-offline-bg': '#0f172a',
  '--wp-state-offline-fg': '#cbd5e1',

  '--wp-state-unknown': '#8b5cf6',
  '--wp-state-unknown-bg': '#2e1065',
  '--wp-state-unknown-fg': '#ddd6fe',

  '--wp-prio-critical': '#ef4444',
  '--wp-prio-high': '#f97316',
  '--wp-prio-medium': '#eab308',
  '--wp-prio-low': '#3b82f6',
  '--wp-prio-info': '#64748b',
};

export const lightTheme: ThemeVariables = {
  '--wp-bg': '#f8fafc',
  '--wp-surface': '#ffffff',
  '--wp-surface-raised': '#f1f5f9',
  '--wp-border': '#cbd5e1',
  '--wp-text': '#0f172a',
  '--wp-text-muted': '#64748b',
  '--wp-text-inverse': '#ffffff',

  '--wp-state-alarm': '#dc2626',
  '--wp-state-alarm-bg': '#fee2e2',
  '--wp-state-alarm-fg': '#7f1d1d',

  '--wp-state-fault': '#ea580c',
  '--wp-state-fault-bg': '#ffedd5',
  '--wp-state-fault-fg': '#7c2d12',

  '--wp-state-warning': '#ca8a04',
  '--wp-state-warning-bg': '#fef9c3',
  '--wp-state-warning-fg': '#713f12',

  '--wp-state-normal-active': '#059669',
  '--wp-state-normal-active-bg': '#d1fae5',
  '--wp-state-normal-active-fg': '#064e3b',

  '--wp-state-normal': '#16a34a',
  '--wp-state-normal-bg': '#dcfce7',
  '--wp-state-normal-fg': '#14532d',

  '--wp-state-maintenance': '#0891b2',
  '--wp-state-maintenance-bg': '#cffafe',
  '--wp-state-maintenance-fg': '#164e63',

  '--wp-state-disabled': '#475569',
  '--wp-state-disabled-bg': '#f1f5f9',
  '--wp-state-disabled-fg': '#1e293b',

  '--wp-state-offline': '#334155',
  '--wp-state-offline-bg': '#e2e8f0',
  '--wp-state-offline-fg': '#0f172a',

  '--wp-state-unknown': '#7c3aed',
  '--wp-state-unknown-bg': '#ede9fe',
  '--wp-state-unknown-fg': '#4c1d95',

  '--wp-prio-critical': '#dc2626',
  '--wp-prio-high': '#ea580c',
  '--wp-prio-medium': '#ca8a04',
  '--wp-prio-low': '#2563eb',
  '--wp-prio-info': '#475569',
};

export const highContrastTheme: ThemeVariables = {
  '--wp-bg': '#000000',
  '--wp-surface': '#000000',
  '--wp-surface-raised': '#121212',
  '--wp-border': '#ffffff',
  '--wp-text': '#ffffff',
  '--wp-text-muted': '#e0e0e0',
  '--wp-text-inverse': '#000000',

  '--wp-state-alarm': '#ff0000',
  '--wp-state-alarm-bg': '#000000',
  '--wp-state-alarm-fg': '#ff6666',

  '--wp-state-fault': '#ff8800',
  '--wp-state-fault-bg': '#000000',
  '--wp-state-fault-fg': '#ffaa44',

  '--wp-state-warning': '#ffff00',
  '--wp-state-warning-bg': '#000000',
  '--wp-state-warning-fg': '#ffff33',

  '--wp-state-normal-active': '#00ff66',
  '--wp-state-normal-active-bg': '#000000',
  '--wp-state-normal-active-fg': '#33ff88',

  '--wp-state-normal': '#00ff00',
  '--wp-state-normal-bg': '#000000',
  '--wp-state-normal-fg': '#66ff66',

  '--wp-state-maintenance': '#00ffff',
  '--wp-state-maintenance-bg': '#000000',
  '--wp-state-maintenance-fg': '#66ffff',

  '--wp-state-disabled': '#aaaaaa',
  '--wp-state-disabled-bg': '#000000',
  '--wp-state-disabled-fg': '#cccccc',

  '--wp-state-offline': '#888888',
  '--wp-state-offline-bg': '#000000',
  '--wp-state-offline-fg': '#bbbbbb',

  '--wp-state-unknown': '#cc88ff',
  '--wp-state-unknown-bg': '#000000',
  '--wp-state-unknown-fg': '#ddaaff',

  '--wp-prio-critical': '#ff0000',
  '--wp-prio-high': '#ff8800',
  '--wp-prio-medium': '#ffff00',
  '--wp-prio-low': '#3388ff',
  '--wp-prio-info': '#ffffff',
};

export const themes = {
  dark: darkTheme,
  light: lightTheme,
  'high-contrast': highContrastTheme,
} as const;

export type ThemeName = keyof typeof themes;

export function applyTheme(themeName: ThemeName, element: HTMLElement = document.documentElement): void {
  const selected = themes[themeName] ?? themes.dark;
  for (const [key, value] of Object.entries(selected)) {
    element.style.setProperty(key, value);
  }
  element.setAttribute('data-theme', themeName);
}
