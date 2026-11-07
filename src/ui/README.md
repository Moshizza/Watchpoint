# src/ui — TypeScript UI (pnpm workspace)

`packages/<name>` → `@watchpoint/<name>` libraries; `apps/desktop` (Electron shell), `apps/desktop-ui`,
`apps/web`. Conventions: [Architecture README §7](../../Architecture/README.md#7-conventions--typescript--ui), [spec UIC §3](../../Architecture/specs/UIC-ui-core.md#3-workspace-t008).

This folder is itself the private package `@watchpoint/ui-workspace`, which holds the shared tooling
(TypeScript base config, ESLint with the `wp/*` rules, Prettier, Vitest projects, Tailwind preset).
From the repository root (Node 24, pnpm from `packageManager`):

```bash
pnpm install
pnpm run lint
pnpm run typecheck
pnpm run test
```

A new package needs a `package.json` (`"type": "module"`, `exports`), a `tsconfig.json` extending
`../../tsconfig.base.json`, and at least one `*.test.ts` whose test names start with a test id.
