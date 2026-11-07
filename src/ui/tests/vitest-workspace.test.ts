import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, relative } from 'node:path';
import { promisify } from 'node:util';
import { beforeAll, expect, it } from 'vitest';
import { packageFolders, workspaceRoot } from './workspace-paths.ts';

const run = promisify(execFile);
const vitest = join(createRequire(import.meta.url).resolve('vitest/package.json'), '..', 'vitest.mjs');

interface ListedFile {
  file: string;
  projectName?: string;
}

let listed: ListedFile[];

beforeAll(async () => {
  const { stdout } = await run(process.execPath, [vitest, 'list', '--filesOnly', '--json'], { cwd: workspaceRoot });
  listed = JSON.parse(stdout) as ListedFile[];
});

it.each(packageFolders)('UIC-U-003 package %s is a Vitest project with tests', (folder) => {
  const name = (
    JSON.parse(readFileSync(join(workspaceRoot, 'packages', folder, 'package.json'), 'utf8')) as { name: string }
  ).name;
  const files = listed
    .filter((entry) => entry.projectName === name)
    .map((entry) => relative(workspaceRoot, entry.file));
  expect(files.length).toBeGreaterThan(0);
  expect(files.every((file) => file.startsWith(join('packages', folder, 'src')))).toBe(true);
});
