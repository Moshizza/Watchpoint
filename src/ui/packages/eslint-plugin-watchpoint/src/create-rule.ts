import { ESLintUtils } from '@typescript-eslint/utils';

/** Creates a `wp/*` rule with a documentation link to the UI core spec. */
export const createRule = ESLintUtils.RuleCreator(
  () =>
    'https://github.com/Moshizza/Watchpoint/blob/main/Architecture/specs/UIC-ui-core.md#31-custom-lint-rules-eslint-plugin-watchpoint-local-package',
);
