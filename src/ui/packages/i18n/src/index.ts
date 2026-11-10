import i18next, { type i18n, type TFunction } from 'i18next';
import ICU from 'i18next-icu';
import { useCallback, useSyncExternalStore } from 'react';
import { initReactI18next } from 'react-i18next';

import appAr from '../locales/ar/app.json' with { type: 'json' };
import commonAr from '../locales/ar/common.json' with { type: 'json' };
import errorsAr from '../locales/ar/errors.json' with { type: 'json' };
import appEn from '../locales/en/app.json' with { type: 'json' };
import commonEn from '../locales/en/common.json' with { type: 'json' };
import errorsEn from '../locales/en/errors.json' with { type: 'json' };
import appFr from '../locales/fr/app.json' with { type: 'json' };
import commonFr from '../locales/fr/common.json' with { type: 'json' };
import errorsFr from '../locales/fr/errors.json' with { type: 'json' };
import { pseudoArXB, pseudoEnXA } from './pseudo.ts';
import type { ApiError, NetworkError } from './types.ts';

export const packageName = '@watchpoint/i18n';

export * from './pseudo.ts';
export * from './types.ts';

export const SUPPORTED_CULTURES = ['en', 'fr', 'ar', 'en-XA', 'ar-XB'] as const;
export type SupportedCulture = (typeof SUPPORTED_CULTURES)[number];

const FSI = '\u2068';
const PDI = '\u2069';

export function bidiIsolate(text: string): string {
  return `${FSI}${text}${PDI}`;
}

export function isRtl(culture: string): boolean {
  const norm = culture.toLowerCase().split('-')[0];
  return norm === 'ar' || culture === 'ar-XB';
}

function transformBundle(bundle: Record<string, unknown>, transformer: (s: string) => string): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(bundle)) {
    if (typeof value === 'string') {
      result[key] = transformer(value);
    } else if (value && typeof value === 'object' && !Array.isArray(value)) {
      result[key] = transformBundle(value as Record<string, unknown>, transformer);
    } else {
      result[key] = value;
    }
  }
  return result;
}

const resources = {
  en: { app: appEn, common: commonEn, errors: errorsEn },
  fr: { app: appFr, common: commonFr, errors: errorsFr },
  ar: { app: appAr, common: commonAr, errors: errorsAr },
  'en-XA': {
    app: transformBundle(appEn as Record<string, unknown>, pseudoEnXA),
    common: transformBundle(commonEn as Record<string, unknown>, pseudoEnXA),
    errors: transformBundle(errorsEn as Record<string, unknown>, pseudoEnXA),
  },
  'ar-XB': {
    app: transformBundle(appAr as Record<string, unknown>, pseudoArXB),
    common: transformBundle(commonAr as Record<string, unknown>, pseudoArXB),
    errors: transformBundle(errorsAr as Record<string, unknown>, pseudoArXB),
  },
};

export interface InitI18nOptions {
  culture?: string;
  fetchTypesBundle?: (culture: string) => Promise<object>;
}

export interface CachedBundle {
  etag: string | undefined;
  data: object;
}

const typesBundleCache = new Map<string, CachedBundle>();

export async function fetchWithEtagCache(
  culture: string,
  fetcher?: (culture: string) => Promise<{ etag?: string; data: object } | object>,
): Promise<object> {
  const cached = typesBundleCache.get(culture);
  if (!fetcher) {
    return cached?.data ?? {};
  }
  const result = await fetcher(culture);
  if ('data' in result && typeof result === 'object' && result !== null) {
    const withEtag = result as { etag?: string; data: object };
    typesBundleCache.set(culture, { etag: withEtag.etag, data: withEtag.data });
    return withEtag.data;
  }
  typesBundleCache.set(culture, { etag: cached?.etag, data: result });
  return result;
}

let i18nInstance: i18n | null = null;
const listeners = new Set<() => void>();

function notify() {
  for (const cb of listeners) {
    cb();
  }
}

export async function initI18n(options?: InitI18nOptions): Promise<i18n> {
  const culture = options?.culture ?? 'en';
  const instance = i18next.createInstance();
  const icu = new ICU();

  await instance
    .use(icu)
    .use(initReactI18next)
    .init({
      lng: culture,
      fallbackLng: 'en',
      supportedLngs: SUPPORTED_CULTURES,
      defaultNS: 'app',
      ns: ['app', 'common', 'errors', 'types'],
      resources,
      interpolation: {
        escapeValue: false,
      },
      parseMissingKeyHandler(key) {
        const isProd =
          typeof globalThis !== 'undefined' &&
          'process' in globalThis &&
          (globalThis as { process?: { env?: Record<string, string> } }).process?.env?.['NODE_ENV'] === 'production';
        if (!isProd) {
          return `⟦${key}⟧`;
        }
        return key;
      },
      saveMissing: true,
      missingKeyHandler() {
        const isProd =
          typeof globalThis !== 'undefined' &&
          'process' in globalThis &&
          (globalThis as { process?: { env?: Record<string, string> } }).process?.env?.['NODE_ENV'] === 'production';
        if (!isProd) {
          // Dev missing key handler
        }
      },
    });

  if (options?.fetchTypesBundle) {
    try {
      const types = await options.fetchTypesBundle(culture);
      instance.addResourceBundle(culture, 'types', types, true, true);
    } catch {
      // Ignore initial types fetch errors
    }
  }

  if (typeof document !== 'undefined' && document.documentElement) {
    document.documentElement.lang = culture;
    document.documentElement.dir = isRtl(culture) ? 'rtl' : 'ltr';
  }

  i18nInstance = instance;
  instance.on('languageChanged', (lng) => {
    if (typeof document !== 'undefined' && document.documentElement) {
      document.documentElement.lang = lng;
      document.documentElement.dir = isRtl(lng) ? 'rtl' : 'ltr';
    }
    notify();
  });

  return instance;
}

export function getI18n(): i18n {
  if (!i18nInstance) {
    const instance = i18next.createInstance();
    const icu = new ICU();
    void instance.use(icu).init({
      lng: 'en',
      fallbackLng: 'en',
      supportedLngs: SUPPORTED_CULTURES,
      defaultNS: 'app',
      ns: ['app', 'common', 'errors', 'types'],
      resources,
      interpolation: { escapeValue: false },
    });
    i18nInstance = instance;
  }
  return i18nInstance;
}

export function useT(ns = 'app'): (key: string, args?: Record<string, unknown>) => string {
  const inst = getI18n();
  useSyncExternalStore(
    (onStoreChange) => {
      listeners.add(onStoreChange);
      return () => {
        listeners.delete(onStoreChange);
      };
    },
    () => inst.language,
  );

  return useCallback(
    (key: string, args?: Record<string, unknown>): string => {
      let isolatedArgs: Record<string, unknown> | undefined = args;
      if (args) {
        isolatedArgs = {};
        for (const [k, v] of Object.entries(args)) {
          if (typeof v === 'string') {
            isolatedArgs[k] = bidiIsolate(v);
          } else {
            isolatedArgs[k] = v;
          }
        }
      }
      return inst.t(key, { ns, ...isolatedArgs });
    },
    [inst, ns],
  );
}

export function useCulture(): { culture: string; dir: 'ltr' | 'rtl'; setCulture(c: string): Promise<void> } {
  const inst = getI18n();
  const culture = useSyncExternalStore(
    (onStoreChange) => {
      listeners.add(onStoreChange);
      return () => {
        listeners.delete(onStoreChange);
      };
    },
    () => inst.language || 'en',
  );

  const setCulture = useCallback(
    async (c: string) => {
      await inst.changeLanguage(c);
      if (typeof document !== 'undefined' && document.documentElement) {
        document.documentElement.lang = c;
        document.documentElement.dir = isRtl(c) ? 'rtl' : 'ltr';
      }
      notify();
    },
    [inst],
  );

  return {
    culture,
    dir: isRtl(culture) ? 'rtl' : 'ltr',
    setCulture,
  };
}

export function translateError(
  e: ApiError | NetworkError,
  t: TFunction,
): { title: string; fields: Record<string, string> } {
  const code = e.code;
  const args = ('args' in e && e.args ? e.args : {}) as Record<string, unknown>;

  const isolatedArgs: Record<string, unknown> = { ns: 'errors' };
  for (const [k, v] of Object.entries(args)) {
    isolatedArgs[k] = typeof v === 'string' ? bidiIsolate(v) : v;
  }

  const titleKey = `errors:${code}`;
  const translatedTitle = t(titleKey, isolatedArgs);
  const title =
    translatedTitle !== titleKey && !translatedTitle.startsWith('⟦')
      ? translatedTitle
      : 'message' in e && e.message
        ? e.message
        : code;

  const fields: Record<string, string> = {};
  if ('fieldErrors' in e && e.fieldErrors) {
    for (const [field, fieldCode] of Object.entries(e.fieldErrors)) {
      const fieldKey = `errors:${fieldCode}`;
      const translatedField = t(fieldKey, { ns: 'errors', field });
      fields[field] = translatedField !== fieldKey && !translatedField.startsWith('⟦') ? translatedField : fieldCode;
    }
  }

  return { title, fields };
}

export function formatDateTime(
  d: Date | string,
  opts: { culture: string; timeZone: string; style?: 'short' | 'medium' | 'long' },
): string {
  const date = typeof d === 'string' ? new Date(d) : d;
  const style = opts.style ?? 'medium';

  let dateStyle: 'short' | 'medium' | 'long' = 'medium';
  let timeStyle: 'short' | 'medium' | 'long' = 'medium';

  if (style === 'short') {
    dateStyle = 'short';
    timeStyle = 'short';
  } else if (style === 'long') {
    dateStyle = 'long';
    timeStyle = 'long';
  }

  const baseCulture = opts.culture === 'ar' ? 'ar-u-nu-latn' : opts.culture;

  return new Intl.DateTimeFormat(baseCulture, {
    dateStyle,
    timeStyle,
    timeZone: opts.timeZone,
  }).format(date);
}

export function formatNumber(
  n: number,
  opts: { culture: string; unit?: string; decimals?: number; numberingSystem?: string },
): string {
  const culture = opts.culture;
  const numberingSystem = opts.numberingSystem ?? (culture.startsWith('ar') ? 'latn' : undefined);

  const formatterOpts: Intl.NumberFormatOptions = {};

  if (opts.decimals !== undefined) {
    formatterOpts.minimumFractionDigits = opts.decimals;
    formatterOpts.maximumFractionDigits = opts.decimals;
  }

  if (numberingSystem) {
    formatterOpts.numberingSystem = numberingSystem;
  }

  if (opts.unit) {
    const numStr = new Intl.NumberFormat(culture, formatterOpts).format(n);
    return `${numStr} ${opts.unit}`;
  }

  return new Intl.NumberFormat(culture, formatterOpts).format(n);
}

export function formatDuration(iso: string, culture: string): string {
  const regex = /P(?:([0-9]+)D)?(?:T(?:([0-9]+)H)?(?:([0-9]+)M)?(?:([0-9]+)S)?)?/;
  const matches = regex.exec(iso);
  if (!matches) return iso;

  const days = parseInt(matches[1] ?? '0', 10);
  const hours = parseInt(matches[2] ?? '0', 10);
  const minutes = parseInt(matches[3] ?? '0', 10);
  const seconds = parseInt(matches[4] ?? '0', 10);

  const parts: string[] = [];

  if (culture.startsWith('fr')) {
    if (days > 0) parts.push(`${days} j`);
    if (hours > 0) parts.push(`${hours} h`);
    if (minutes > 0) parts.push(`${minutes} min`);
    if (seconds > 0 || parts.length === 0) parts.push(`${seconds} s`);
  } else if (culture.startsWith('ar')) {
    if (days > 0) parts.push(`${days} ي`);
    if (hours > 0) parts.push(`${hours} س`);
    if (minutes > 0) parts.push(`${minutes} د`);
    if (seconds > 0 || parts.length === 0) parts.push(`${seconds} ث`);
  } else {
    if (days > 0) parts.push(`${days} d`);
    if (hours > 0) parts.push(`${hours} h`);
    if (minutes > 0) parts.push(`${minutes} min`);
    if (seconds > 0 || parts.length === 0) parts.push(`${seconds} s`);
  }

  return parts.join(' ');
}
