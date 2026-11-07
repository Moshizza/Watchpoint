import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import wp from 'eslint-plugin-watchpoint';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// ESLint configuration of the UI workspace (spec UIC §3, §3.1).
export default defineConfig(
  globalIgnores(['**/node_modules/', '**/dist/', '**/coverage/', 'packages/eslint-plugin-watchpoint/fixtures/']),
  js.configs.recommended,
  tseslint.configs.strict,
  reactHooks.configs.flat['recommended-latest'],
  jsxA11y.flatConfigs.strict,
  {
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.browser },
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: { wp },
    rules: {
      'wp/no-literal-strings': 'error',
      'wp/no-physical-direction-classes': 'error',
      'wp/no-direct-fetch': 'error',
    },
  },
  {
    // The REST client is the only place allowed to call fetch().
    files: ['packages/api-client/**'],
    rules: { 'wp/no-direct-fetch': 'off' },
  },
  {
    files: ['**/*.test.{ts,tsx}', 'tests/**'],
    rules: { 'wp/test-id-name': 'error' },
  },
  {
    // Tooling and configuration files run in Node.
    files: ['*.{ts,js}', 'tailwind/**', 'tests/**', 'packages/eslint-plugin-watchpoint/**'],
    languageOptions: { globals: { ...globals.node } },
  },
);
