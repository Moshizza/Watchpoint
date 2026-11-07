import { ESLint } from 'eslint';
import { join } from 'node:path';
import { beforeAll, expect, it } from 'vitest';
import { workspaceRoot } from './workspace-paths.ts';

const fixtures = join(workspaceRoot, 'packages', 'eslint-plugin-watchpoint', 'fixtures');
let results: Map<string, string[]>;

beforeAll(async () => {
  // The real workspace configuration; ignore: false because the fixtures are excluded from the normal run.
  const eslint = new ESLint({ cwd: workspaceRoot, ignore: false });
  const lint = await eslint.lintFiles([join(fixtures, '*.{ts,tsx}')]);
  results = new Map(
    lint.map((result) => [
      result.filePath.slice(fixtures.length + 1),
      result.messages.map((message) => message.ruleId ?? `fatal: ${message.message}`),
    ]),
  );
});

it.each([
  ['literal-strings.tsx', 'wp/no-literal-strings'],
  ['physical-classes.tsx', 'wp/no-physical-direction-classes'],
  ['direct-fetch.ts', 'wp/no-direct-fetch'],
  ['test-names.test.ts', 'wp/test-id-name'],
])('UIC-U-002 fixture %s triggers %s', (file, rule) => {
  expect(results.get(file)).toContain(rule);
});

it('UIC-U-002 a fixture following the conventions has no problems', () => {
  expect(results.get('clean.tsx')).toEqual([]);
});
