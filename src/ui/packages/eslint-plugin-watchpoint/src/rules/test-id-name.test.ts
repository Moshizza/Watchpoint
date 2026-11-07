import { ruleTester } from '../rule-tester.ts';
import { testIdName } from './test-id-name.ts';

ruleTester.run('test-id-name', testIdName, {
  valid: [
    'it("UIC-U-012 subscribe receives the snapshot", () => {});',
    'test("X-API-REG-C-001 contract", () => {});',
    'it.only("UIC-U-001 strict build", () => {});',
    'it.each([1, 2])("UIC-U-003 case %s", () => {});',
    'test(`UIC-U-004 generated types`, () => {});',
    'describe("api client", () => {});',
    'it(name, () => {});',
  ],
  invalid: [
    { code: 'it("adds headers", () => {});', errors: [{ messageId: 'missingId' }] },
    { code: 'test.skip("uic-u-005 lower case", () => {});', errors: [{ messageId: 'missingId' }] },
    { code: 'it("UIC-U-12 too short", () => {});', errors: [{ messageId: 'missingId' }] },
    { code: 'it.each([1])("case %s", () => {});', errors: [{ messageId: 'missingId' }] },
    { code: 'it("UIC-U-005adds", () => {});', errors: [{ messageId: 'missingId' }] },
  ],
});
