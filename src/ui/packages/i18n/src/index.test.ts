import { expect, it } from 'vitest';
import { packageName } from './index.ts';

it('UIC-U-003 i18n tests run in the Vitest workspace', () => {
  expect(packageName).toBe('@watchpoint/i18n');
});
