import { defineProject } from 'vitest/config';

// fixtures/ holds lint fixtures named *.test.ts on purpose (UIC-U-002); they are not tests.
export default defineProject({
  test: {
    include: ['src/**/*.test.ts'],
  },
});
