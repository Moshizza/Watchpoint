# Spike T0.11 — connector isolation

Compares a connector loaded **in-process** in a collectible `AssemblyLoadContext` with the same
connector in an **isolated host process** linked to the node by gRPC over a Unix domain socket
(IRT §5, IHO §4–§6). Results and decision: [ADR-003](../../../Architecture/decisions/ADR-003-connector-isolation.md).

| Project | Role |
|---|---|
| `Contract` | Shared assembly (loaded from the default context in both processes): connector interface, observation, `PackageLoadContext` |
| `Connector` | The "package": a synthetic connector, never referenced by the others, loaded from `connector/` |
| `Host` | The isolated host process: gRPC client, batching sink, loads the connector in its own load context |
| `Runner` | The "node": gRPC server (`HostLink`), ingest channel, scenarios, Markdown report |

```bash
dotnet run -c Release --project tools/spikes/ConnectorIsolation/Runner -- --out report.md
dotnet run -c Release --project tools/spikes/ConnectorIsolation/Runner -- --quick   # smaller counts
```

Results of the run used by the ADR: [`results-linux-x64.md`](results-linux-x64.md). Linux only (memory from `/proc`, Unix domain socket). Spike code: not production quality, not shipped.
