import { describe, expect, it } from 'vitest';
import {
  bidiIsolate,
  fetchWithEtagCache,
  formatDateTime,
  formatDuration,
  formatNumber,
  initI18n,
  translateError,
} from './index.ts';

import appAr from '../locales/ar/app.json' with { type: 'json' };
import commonAr from '../locales/ar/common.json' with { type: 'json' };
import errorsAr from '../locales/ar/errors.json' with { type: 'json' };
import appEn from '../locales/en/app.json' with { type: 'json' };
import commonEn from '../locales/en/common.json' with { type: 'json' };
import errorsEn from '../locales/en/errors.json' with { type: 'json' };
import appFr from '../locales/fr/app.json' with { type: 'json' };
import commonFr from '../locales/fr/common.json' with { type: 'json' };
import errorsFr from '../locales/fr/errors.json' with { type: 'json' };

describe('i18n tests', () => {
  it('UIC-U-031 culture switch sets lang and dir', async () => {
    const inst = await initI18n({ culture: 'en' });
    expect(document.documentElement.lang).toBe('en');
    expect(document.documentElement.dir).toBe('ltr');

    await inst.changeLanguage('ar');
    expect(document.documentElement.lang).toBe('ar');
    expect(document.documentElement.dir).toBe('rtl');

    await inst.changeLanguage('fr');
    expect(document.documentElement.lang).toBe('fr');
    expect(document.documentElement.dir).toBe('ltr');
  });

  it('UIC-U-032 ICU plurals in en/fr/ar', async () => {
    const inst = await initI18n({ culture: 'en' });

    // English: 0 -> "No devices", 1 -> "1 device", 5 -> "5 devices"
    expect(inst.t('devices.count', { count: 0 })).toBe('No devices');
    expect(inst.t('devices.count', { count: 1 })).toBe('1 device');
    expect(inst.t('devices.count', { count: 5 })).toBe('5 devices');

    // French: 0 -> "Aucun appareil", 1 -> "1 appareil", 5 -> "5 appareils"
    await inst.changeLanguage('fr');
    expect(inst.t('devices.count', { count: 0 })).toBe('Aucun appareil');
    expect(inst.t('devices.count', { count: 1 })).toBe('1 appareil');
    expect(inst.t('devices.count', { count: 5 })).toBe('5 appareils');

    // Arabic: 6 plural forms (zero: 0, one: 1, two: 2, few: 3-10, many: 11-99, other: 100)
    await inst.changeLanguage('ar');
    expect(inst.t('devices.count', { count: 0 })).toBe('لا توجد أجهزة');
    expect(inst.t('devices.count', { count: 1 })).toBe('جهاز واحد');
    expect(inst.t('devices.count', { count: 2 })).toBe('جهازان');
    expect(inst.t('devices.count', { count: 3 })).toBe('3 أجهزة');
    expect(inst.t('devices.count', { count: 25 })).toBe('25 جهازاً');
    expect(inst.t('devices.count', { count: 100 })).toBe('100 جهاز');
  });

  it('UIC-U-033 missing key: dev marker, prod fallback to en', async () => {
    const proc = (globalThis as unknown as { process?: { env?: Record<string, string> } }).process;
    const originalEnv = proc?.env?.['NODE_ENV'];
    try {
      if (proc?.env) proc.env['NODE_ENV'] = 'development';
      const inst = await initI18n({ culture: 'fr' });
      expect(inst.t('missing.some_key')).toBe('⟦missing.some_key⟧');

      if (proc?.env) proc.env['NODE_ENV'] = 'production';
      const prodInst = await initI18n({ culture: 'fr' });
      prodInst.addResourceBundle('en', 'custom_ns', { fallback_key: 'Fallback Value' });
      expect(prodInst.t('fallback_key', { ns: 'custom_ns' })).toBe('Fallback Value');
    } finally {
      if (proc?.env && originalEnv !== undefined) proc.env['NODE_ENV'] = originalEnv;
    }
  });

  it('UIC-U-034 types bundle fetched and cached by ETag', async () => {
    let fetchCount = 0;
    const mockFetcher = async () => {
      fetchCount++;
      return {
        etag: '"12345"',
        data: {
          devices: {
            generic: { label: 'Generic Device' },
          },
        },
      };
    };

    const data1 = await fetchWithEtagCache('en', mockFetcher);
    expect(data1).toEqual({ devices: { generic: { label: 'Generic Device' } } });
    expect(fetchCount).toBe(1);

    const data2 = await fetchWithEtagCache('en');
    expect(data2).toEqual({ devices: { generic: { label: 'Generic Device' } } });
  });

  it('UIC-U-035 translateError uses errors.<code> with args and field messages', async () => {
    const inst = await initI18n({ culture: 'en' });
    const apiError = {
      status: 404,
      code: 'registry.device.not_found',
      messageKey: 'errors.registry.device.not_found',
      args: { deviceId: 'dev-101' },
      fieldErrors: {
        '/settings/port': 'common.value.out_of_range',
      },
    };

    const res = translateError(apiError, inst.t.bind(inst));
    expect(res.title).toContain('Device \u2068dev-101\u2069 was not found.');
    expect(res.fields['/settings/port']).toContain('Value must be between');
  });

  it('UIC-U-036 arguments bidi-isolated by default', () => {
    const isolated = bidiIsolate('Lobby Gate');
    expect(isolated).toBe('\u2068Lobby Gate\u2069');
    expect(isolated.startsWith('\u2068')).toBe(true);
    expect(isolated.endsWith('\u2069')).toBe(true);
  });

  it('UIC-U-037 date formatting in site vs user time zone', () => {
    const fixedDate = new Date('2026-11-03T14:30:00.000Z');

    const formattedUtc = formatDateTime(fixedDate, {
      culture: 'en-US',
      timeZone: 'UTC',
      style: 'short',
    });

    const formattedTokyo = formatDateTime(fixedDate, {
      culture: 'en-US',
      timeZone: 'Asia/Tokyo',
      style: 'short',
    });

    expect(formattedUtc).not.toBe(formattedTokyo);
    expect(formattedTokyo).toContain('11/3/26');
  });

  it('UIC-U-038 number formatting: Western digits default for ar; arab option', () => {
    const number = 12345.67;

    const formattedDefault = formatNumber(number, { culture: 'ar' });
    expect(formattedDefault).toMatch(/12.*345/);

    const formattedArab = formatNumber(number, { culture: 'ar', numberingSystem: 'arab' });
    expect(formattedArab).toMatch(/[١٢٣٤٥]/);
  });

  it('UIC-U-039 duration formatting localised', () => {
    const duration = 'PT1H30M15S';

    const enFormatted = formatDuration(duration, 'en');
    expect(enFormatted).toBe('1 h 30 min 15 s');

    const frFormatted = formatDuration(duration, 'fr');
    expect(frFormatted).toBe('1 h 30 min 15 s');

    const arFormatted = formatDuration(duration, 'ar');
    expect(arFormatted).toBe('1 س 30 د 15 ث');
  });

  it('UIC-U-040 pseudo-locales render without untranslated text in UIC components', async () => {
    const instEnXA = await initI18n({ culture: 'en-XA' });
    const textXA = instEnXA.t('devices.list.title');
    expect(textXA).toMatch(/\[.*~.*\]/);

    const instArXB = await initI18n({ culture: 'ar-XB' });
    const textXB = instArXB.t('devices.list.title');
    expect(textXB).toMatch(/⟦.*⟧/);
  });

  it('UIC-U-041 every key used in code exists in en, fr, ar (static extraction check)', () => {
    function getAllKeys(obj: Record<string, unknown>, prefix = ''): string[] {
      let keys: string[] = [];
      for (const [k, v] of Object.entries(obj)) {
        const fullKey = prefix ? `${prefix}.${k}` : k;
        if (typeof v === 'object' && v !== null && !Array.isArray(v)) {
          keys = keys.concat(getAllKeys(v as Record<string, unknown>, fullKey));
        } else {
          keys.push(fullKey);
        }
      }
      return keys.sort();
    }

    const enAppKeys = getAllKeys(appEn);
    const frAppKeys = getAllKeys(appFr);
    const arAppKeys = getAllKeys(appAr);

    expect(frAppKeys).toEqual(enAppKeys);
    expect(arAppKeys).toEqual(enAppKeys);

    const enCommonKeys = getAllKeys(commonEn);
    const frCommonKeys = getAllKeys(commonFr);
    const arCommonKeys = getAllKeys(commonAr);

    expect(frCommonKeys).toEqual(enCommonKeys);
    expect(arCommonKeys).toEqual(enCommonKeys);

    const enErrorKeys = getAllKeys(errorsEn);
    const frErrorKeys = getAllKeys(errorsFr);
    const arErrorKeys = getAllKeys(errorsAr);

    expect(frErrorKeys).toEqual(enErrorKeys);
    expect(arErrorKeys).toEqual(enErrorKeys);
  });
});
