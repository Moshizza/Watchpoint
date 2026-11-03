# Watchpoint

Watchpoint is a security hypervision platform (PSIM). It integrates access control, video,
intrusion, fire, intercom, building management and smart-city systems, and gives operators
one place to supervise devices, handle alarms, send commands, watch video and automate
reactions.

The project has finished its design phase and is being built task by task.

- Product and design documentation: [docs/](docs/README.md)
- Implementation specs, build order and task sheet: [Architecture/](Architecture/README.md)
- Rules for AI agents working on the repository: [CLAUDE.md](CLAUDE.md)

## Getting started (developers)

| Tool | Version |
|---|---|
| .NET SDK | 10.0.1xx (pinned in `global.json`) |
| Node.js | 24 LTS (`.nvmrc`) |
| pnpm | 10 (`packageManager` in `package.json`; `corepack enable` installs it) |
| Docker | Recent version, for Testcontainers-based tests |

```sh
dotnet build src/service/Watchpoint.slnx   # .NET solution
pnpm install                               # TypeScript workspace (src/ui, src/mobile)
```
