import { ruleTester } from '../rule-tester.ts';
import { noDirectFetch } from './no-direct-fetch.ts';

ruleTester.run('no-direct-fetch', noDirectFetch, {
  valid: [
    'import { fetch } from "./http"; fetch("/x");',
    'function fetch(url: string) { return url; } fetch("/x");',
    'client.fetch("/x");',
    'api.GET("/devices");',
  ],
  invalid: [
    { code: 'fetch("/api/devices");', errors: [{ messageId: 'directFetch' }] },
    { code: 'await window.fetch("/api");', errors: [{ messageId: 'directFetch' }] },
    { code: 'globalThis["fetch"]("/api");', errors: [{ messageId: 'directFetch' }] },
  ],
});
