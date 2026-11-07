import { defineConfig } from 'vitest/config';

// One Vitest project per package (spec UIC §3) plus the workspace tests in tests/.
// A package needs no configuration of its own; it may add a vitest.config.ts (e.g. happy-dom for React).
export default defineConfig({
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
