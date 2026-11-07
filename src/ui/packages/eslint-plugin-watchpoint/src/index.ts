import type { TSESLint } from '@typescript-eslint/utils';
import type { ESLint } from 'eslint';
import { noDirectFetch } from './rules/no-direct-fetch.ts';
import { noLiteralStrings } from './rules/no-literal-strings.ts';
import { noPhysicalDirectionClasses } from './rules/no-physical-direction-classes.ts';
import { testIdName } from './rules/test-id-name.ts';

/** The `wp/*` rules (spec UIC §3.1). Register the plugin under the prefix `wp`. */
export const rules = {
  'no-literal-strings': noLiteralStrings,
  'no-physical-direction-classes': noPhysicalDirectionClasses,
  'no-direct-fetch': noDirectFetch,
  'test-id-name': testIdName,
} satisfies Record<string, TSESLint.RuleModule<string, readonly unknown[]>>;

// typescript-eslint rule types and ESLint's own Plugin type differ only in deprecated context members.
const plugin = {
  meta: { name: 'eslint-plugin-watchpoint', version: '0.0.0' },
  rules,
} as unknown as ESLint.Plugin;

export default plugin;
