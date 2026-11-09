import { defineConfig } from 'vitest/config';

// One Vitest project per package (spec UIC §3) plus the workspace tests in tests/.
// A package needs no configuration of its own; it may add a vitest.config.ts (e.g. happy-dom for React).
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  resolve: {
    alias: {
      '@watchpoint/i18n': resolve(__dirname, 'packages/i18n/src/index.ts'),
      '@watchpoint/ui': resolve(__dirname, 'packages/ui/src/index.ts'),
      '@watchpoint/testing': resolve(__dirname, 'packages/testing/src/index.ts'),
    },
  },
  test: {
    projects: [
      'packages/*',
      {
        test: {
          name: 'workspace',
          include: ['tests/**/*.test.ts'],
          testTimeout: 120_000,
        },
      },
    ],
  },
});
