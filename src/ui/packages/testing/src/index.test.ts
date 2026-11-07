import { expect, it } from 'vitest';
import { packageName } from './index.ts';

it('UIC-U-003 testing tests run in the Vitest workspace', () => {
  expect(packageName).toBe('@watchpoint/testing');
});
