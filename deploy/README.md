# deploy

Installer definitions, container images, compose files and the Helm chart. See [spec DEP](../Architecture/specs/DEP-deployment.md).

## Development stack (`dev/`)

PostgreSQL + TimescaleDB and NATS for a node started from the sources ([spec HST §8, §9](../Architecture/specs/HST-host.md#8-development-stack-deploydevcomposeyml)):

```bash
docker compose -f deploy/dev/compose.yml up -d
```

```bash
dotnet run --project src/service/Watchpoint.Host -- --config deploy/dev/watchpoint.dev.json
```

The node listens on `http://localhost:5080`, keeps its data in `.watchpoint-dev/` and loads `dev/seed/sample-site.json`
at its first start. Images and the nats-server version are pinned in [`versions.env`](versions.env).
