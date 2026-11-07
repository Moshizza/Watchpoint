import { execFile } from 'node:child_process';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { describe, expect, it } from 'vitest';
import { packageFolders, workspaceRoot } from './workspace-paths.ts';

const run = promisify(execFile);
const tsc = createRequire(import.meta.url).resolve('typescript/bin/tsc');

interface ShownConfig {
  compilerOptions: Record<string, unknown>;
}

describe.each(packageFolders)('package %s', (folder) => {
  const project = join(workspaceRoot, 'packages', folder, 'tsconfig.json');

  it('UIC-U-001 extends the strict base configuration', async () => {
    const { stdout } = await run(process.execPath, [tsc, '-p', project, '--showConfig']);
    const options = (JSON.parse(stdout) as ShownConfig).compilerOptions;
    expect(options).toMatchObject({
      strict: true,
      noUncheckedIndexedAccess: true,
      exactOptionalPropertyTypes: true,
      moduleResolution: 'bundler',
    });
  });

  it('UIC-U-001 type-checks without errors', async () => {
    await expect(run(process.execPath, [tsc, '-p', project])).resolves.toBeDefined();
  });
});

it('UIC-U-001 workspace tooling type-checks without errors', async () => {
  await expect(run(process.execPath, [tsc, '-p', join(workspaceRoot, 'tsconfig.json')])).resolves.toBeDefined();
});
