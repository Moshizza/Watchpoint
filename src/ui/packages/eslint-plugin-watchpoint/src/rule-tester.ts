import { RuleTester } from '@typescript-eslint/rule-tester';
import { afterAll, describe, it } from 'vitest';

// Run RuleTester cases as Vitest tests whose names carry the test id (Architecture/README.md §9.2).
RuleTester.afterAll = afterAll;
RuleTester.describe = describe;
RuleTester.describeSkip = describe.skip;
RuleTester.it = (name, fn) => it(`UIC-U-002 ${name}`, fn);
RuleTester.itOnly = (name, fn) => it.only(`UIC-U-002 ${name}`, fn);
RuleTester.itSkip = (name, fn) => it.skip(`UIC-U-002 ${name}`, fn);

/** A RuleTester for TypeScript and JSX sources. */
export const ruleTester = new RuleTester({
  languageOptions: { parserOptions: { ecmaFeatures: { jsx: true } } },
});
