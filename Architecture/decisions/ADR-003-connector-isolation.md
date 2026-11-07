# ADR-003 — Connector isolation: in-process load contexts and gRPC isolated hosts

| | |
|---|---|
| Status | Accepted |
| Date | 2026-11-07 |
| Task | T0.11 (spike), issue #11 |
| Specs affected | [IRT §5](../specs/IRT-integration-runtime.md#5-load-contexts), [IHO §5.3, §6.2, §6.4](../specs/IHO-integration-host.md) |
| Harness | [`tools/spikes/ConnectorIsolation`](../../tools/spikes/ConnectorIsolation/README.md), raw results [`results-linux-x64.md`](../../tools/spikes/ConnectorIsolation/results-linux-x64.md) |

## 1. Context

Connectors are third-party code running next to the pipeline. The specs plan two hosting modes:
**in-process** (IRT §5: one collectible `AssemblyLoadContext` per package version, M1) and
**isolated** (IHO: a separate `watchpoint-integration-host` process linked to the node by a gRPC
`HostLink` session, T4.01). IHO §6.4 set performance targets "to be confirmed by spike T0.11". The
questions:

1. What does a connector fault do to the node in each mode?
2. What do isolation and the gRPC link cost (throughput, latency, command round-trip, start, memory)?
3. Is the sink bridge rule of IHO §5.3 ("a batch every 50 ms or 500 items") compatible with the
   latency target?
4. Does unloading an in-process load context work, and what prevents it?

## 2. Options

| Option | Summary |
|---|---|
| A. In-process only | Simplest and fastest; every connector shares the node's fate |
| B. Isolated only | Every connector in a host process; pays the link cost and ~45 MB per host always |
| C. Both, chosen per package/connection (current spec) | In-process for trusted packages (`inProcessAllowed`), isolated for `isolatedRecommended`/`isolatedRequired` and on demand |

## 3. Measurements

Harness: a synthetic connector (the "package") loaded through the IRT §5 `PackageLoadContext`;
the node side is a gRPC server (Kestrel, HTTP/2) on a **Unix domain socket**; the host is a
separate .NET process (gRPC client, `Grpc.Net.Client` 2.85, one bidirectional `Session` stream),
which loads the same connector in its own load context and batches observations into
`ObservationBatch` messages (≤ 500 items). The node's ingest is one bounded channel with one
consumer (same code for both modes); latency = Stopwatch (system-wide `CLOCK_MONOTONIC`) at
report → at ingest. Two batching policies were compared in the host:

- `timer` — the spec's rule: a batch when 500 items wait or 50 ms after the first one;
- `eager` — send as soon as the previous write has completed, with whatever has accumulated (≤ 500).

Machine: Claude cloud container, Ubuntu 24.04, 4 vCPU Intel Xeon (shared, noisy), .NET 10.0.14,
Release build. Two full runs; ranges below cover both.

### 3.1 Faults

| Fault in the connector | In-process | Isolated |
|---|---|---|
| Unhandled exception on a thread it started | **node process dies** (exit 134) | host dies; node sees the link end after 17–29 ms; node unaffected; new host Ready in ~140 ms |
| Stack overflow | **node process dies** | same as above |
| `Environment.FailFast` (≈ native `abort()`) | **node process dies** | same as above |
| Thread spinning forever | node survives but loses a core until restart (threads cannot be aborted, the context cannot unload while code runs) | host heartbeat **still healthy**, commands still answered; the node must decide to kill; kill → link loss seen in 10–14 ms |

No .NET mechanism (load contexts, `AppDomain.UnhandledException`, try/catch around calls) can keep
the process alive after the first three: in-process, **one faulty connector stops every connection,
the pipeline and the API of the node**.

### 3.2 Costs

| Metric (IHO §6.4) | Target | Measured | Verdict |
|---|---|---|---|
| Observation throughput per isolated host | ≥ 50,000/s | 2.0–2.4 M/s (`eager`), 2.8–3.1 M/s (`timer`); in-process 2.1 M/s — the ingest consumer is the limit in all modes | ✅ |
| Added latency report → ingest, p99 | ≤ 5 ms over in-process | `eager`: +2.8 to +4.0 ms (p50 +0.03 to +0.1 ms) at 1k–50k/s; `timer`: **+10 to +53 ms** (p50 +5 to +27 ms) | ✅ with `eager` only |
| Command round-trip overhead | ≤ 2 ms | p50 0.10 ms, p99 0.24–0.26 ms, p99.9 0.8–1.2 ms | ✅ |
| Host start → Ready (excl. package load) | ≤ 2 s | 150 ms median, 240 ms max (incl. a 5 ms connector load) | ✅ |
| Idle memory of an empty host | ≤ 60 MB | 46 MB RSS (43.5 MB with invariant globalization) for the spike host; the real host adds logging, DI and the bridged services | ✅ (to re-check in T4.01) |
| In-process load of a package | — | 3.5 ms, +0.3 MB node RSS | — |

### 3.3 Unloading in-process load contexts

| Case | Result |
|---|---|
| Clean connector, references dropped, `Unload()` | collected after 2 GC rounds (12–36 ms) |
| Connector subscribed to a static event of a **shared** assembly | **never collected** (20 GC rounds): the shared type roots the package's types |

## 4. Decision

1. **Keep option C** (both modes, per package and connection). Isolation is cheap enough to be the
   default wherever trust is not established, and in-process remains the fast path for first-party
   packages.
2. **Default host mode** of a new connection, once isolated hosts exist (T4.01): `Isolated` for
   `isolatedRecommended` packages and for packages not signed by Watchpoint; `InProcess` only for
   Watchpoint-signed `inProcessAllowed` packages; `isolatedRequired` unchanged (never in-process).
3. **Sink bridge batching** (IHO §5.3): replace "every 50 ms or 500 items" by **"send as soon as the
   previous batch is written, with what has accumulated, at most 500 items"**. The 50 ms window
   alone adds 25 ms at p50 and 50 ms at p99, ten times the latency target; eager batching keeps the
   same throughput class and still forms large batches under load (back-pressure of the single
   writer does the batching).
4. **Hang detection**: the host heartbeat cannot see a connector thread that hangs or spins. The node
   kills an isolated host when a connection's requests time out 3 times in a row (IHO §5.2
   timeouts), or when `StopConnection` times out; the in-process host can only report the
   connection `Faulted` and ask for a node restart (HLT shows the stuck connection).
5. **Shared assemblies** (`Watchpoint.Kernel`, `Watchpoint.Integration.Abstractions`) must not expose
   static events, static caches or other static registration points that package code can reach:
   they make load contexts uncollectable (enforced by review; an architecture test can check it once the contract exists).
6. **Transport**: gRPC over a Unix domain socket on Linux is confirmed. Windows (named pipe, as IHO
   §6.2 says, or a Unix domain socket, which Windows 10+ also supports) was **not measured** here;
   T4.01 measures it on the reference Windows machine before fixing the transport there.

## 5. Consequences

- M1 runs connectors in-process only: a connector crash restarts the node service (systemd/Windows
  service recovery). The M1 packages are first-party (simulator, ping, MQTT, Modbus…), which keeps
  the risk acceptable; third-party packages wait for T4.01.
- The in-process host logs the connection/package that owned a crashing thread when it can
  (`AppDomain.UnhandledException` with the load context name), so the restart is attributable.
- IHO §6.4 targets are confirmed on Linux; the `eager` batching rule is part of IHO §5.3.
- The numbers come from a shared 4-vCPU cloud VM: absolute values are indicative; ratios and fault
  behaviour are the result. The harness can be re-run on reference hardware
  (`dotnet run -c Release --project tools/spikes/ConnectorIsolation/Runner -- --out report.md`).
