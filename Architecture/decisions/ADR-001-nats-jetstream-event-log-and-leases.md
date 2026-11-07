# ADR-001 — NATS JetStream as event log and KV lease store

- **Status:** accepted
- **Date:** 2026-11-07
- **Task:** T0.09 ([#9](https://github.com/Moshizza/Watchpoint/issues/9))
- **Affects:** [PIP §5](../specs/PIP-pipeline.md#5-event-log), [CLU §5, §7, §12](../specs/CLU-cluster.md#5-leases-and-fencing-t901), [README §4](../README.md#4-versions-and-tools)
- **Harness:** [`tools/spikes/Watchpoint.Spikes.NatsJetStream`](../../tools/spikes/Watchpoint.Spikes.NatsJetStream) (raw results: [`ADR-001-results.md`](ADR-001-results.md))

## 1. Context

PIP uses one JetStream stream `WP_LOG` (64 partition subjects `wp.log.<pp>.<kind>`) as the
durable, ordered event log; recovery replays it from per-partition checkpoints. CLU uses a
JetStream KV bucket `wp_leases` with compare-and-set (CAS) for leases whose KV revision is the
fencing token, and fences partition writes with an expected-last-sequence check. Before building
M1 on these we needed numbers: append and replay throughput, latency, dedup, fencing behaviour,
KV CAS correctness under contention, and behaviour when nodes die.

## 2. Options

1. **JetStream for both** (spec as written): one infrastructure component, already bundled (HST §7.4).
2. JetStream log + **PostgreSQL** advisory locks / rows for leases: PostgreSQL HA is a separate
   failover domain (CLU §11); a lease store that depends on it couples both failovers.
3. JetStream log with **one stream per partition** (64 streams): makes per-partition replay a
   plain scan, costs 64× stream overhead (Raft groups in R3) and breaks the single global sequence
   that every module uses as checkpoint.

## 3. Method

- nats-server **2.12.1** (official linux-amd64 build, SHA-256 checked), NATS.Net **3.3.2**, .NET 10.
- One host, 4 vCPU, 15 GB RAM, SSD-backed virtual disk. The 3-node cluster runs its three
  servers on the **same host** (shared CPU and disk, loopback network): cluster numbers are a
  lower bound for throughput and an optimistic bound for network latency.
- Record: a representative `property` envelope (PIP §5.2), 420 B JSON + `Nats-Msg-Id`,
  `Wp-Kind`, `Wp-Schema` headers → 629 B stored per record.
- Stream settings as PIP §5.1 (file storage, 2 min duplicate window, 1 MB max message, discard
  old, deny delete/purge), `MaxBytes` 8 GB (sandbox disk).
- Node loss = `SIGKILL` of the server process (no graceful shutdown). Clients use a 1 s
  operation timeout, renew one lease every 100 ms and append one record every ~2 ms.
- Run: `dotnet run -c Release --project tools/spikes/Watchpoint.Spikes.NatsJetStream -- <nats-server> 1000000 report.md`.

## 4. Measurements

### 4.1 Append

| Measurement | R1 (single node) | R3 (3 nodes, one host) |
|---|---|---|
| One record, awaited (durable ack) | p50 0.22 ms, p99 0.55 ms | p50 0.44 ms, p99 0.88 ms |
| Pipelined, 64 / 256 / 1024 acks in flight | 48k / 79k / 93k rec/s | 52k rec/s (1024) |
| Fenced, 64 lanes (one per partition, each awaits its ack) | 55k rec/s | 32k rec/s |
| Fenced **atomic batches** of 100, 32 concurrent | 35k rec/s | 66k rec/s |
| `sync_interval: always` (fsync per write), pipelined | 5.5k rec/s, p50 0.45 ms awaited | — |

The PIP budget (20,000 records/s per node) is met with a large margin by pipelined appends on
R1 and R3, and by fenced appends. **fsync on every write is not viable** (5.5k rec/s).

### 4.2 Dedup and fencing semantics

- Same `Nats-Msg-Id` twice inside the window: second ack `duplicate=true`, same sequence, no
  new record. Re-sending a timed-out append with the **same** record id is therefore safe.
- `Nats-Expected-Last-Subject-Sequence` alone checks the **exact subject**. A partition has one
  subject per kind (`wp.log.17.property`, `wp.log.17.health`…), so a stale writer publishing
  another kind is **not** stopped. With `Nats-Expected-Last-Subject-Sequence-Subject: wp.log.17.>`
  the check covers the whole partition: the stale writer is rejected (`10071 wrong last sequence: N`).
- A fenced append can be rejected although no other writer exists, so a writer must not treat
  every rejection as "fenced out": `10071` after its own earlier attempt was stored (client
  re-send), and `10164 wrong last sequence` (no number; seen once on R3 when the last record on the
  filter **was** the expected one — a transient clustered rejection). Rule: on either code, read
  the last record on `wp.log.<pp>.>`; if it is this writer's own record id, adopt its sequence; if it
  equals the expected sequence, retry; otherwise the writer is fenced out.
- Atomic batches (nats-server 2.12, stream option `AllowAtomicPublish`): the expected-sequence
  headers go on the first message, one ack per batch. 64 concurrent open batches on one stream
  fail with `10176 atomic publish batch is incomplete`; 32 work (the server limits in-flight
  batches per stream). NATS.Net 3.3 has no batch API: the harness uses raw headers
  (`Nats-Batch-Id`, `Nats-Batch-Sequence`, `Nats-Batch-Commit`).

### 4.3 Replay (ordered consumer, 1,000,000 records over 64 partitions, 600 MiB)

| Replay | Rate | Notes |
|---|---|---|
| Whole log (`wp.log.>`) from seq 1 | 176k–203k rec/s | same after server restart |
| One partition (`wp.log.17.>`), from seq 1 or from the middle | 14k–17k rec/s delivered | ≈ 1 M log records scanned per second |
| 16 partitions, one consumer with 16 filters | 39k–45k rec/s delivered | |
| Consumer pull batch 10,000 instead of default | no change | |

Filtered replay is bounded by the server scanning the whole log range, not by delivery: the
cost of replaying one partition is about the cost of scanning the same log range for all of
them. The OS page cache was not dropped (sandbox); a cold disk will be slower.

### 4.4 KV leases (R3)

| Check | Result |
|---|---|
| `Create` on an existing key / `Update` with a stale revision | both rejected (`NatsKVCreateException`, `NatsKVWrongLastRevisionException`) |
| Fencing token monotonic | revision 1 → renew 2 → CAS delete → re-create 4: strictly increasing per key, also across delete |
| 6 clients on 3 nodes race for the same key, 500 rounds, create and take-over update | exactly one winner in every round |
| CAS renew latency | p50 0.37 ms (client on stream leader), 0.48–0.52 ms (on a follower); p99 < 0.9 ms |
| Renewal round of 5,000 leases, 256 CAS in flight (CLU-I-006) | p50 107 ms, max 156 ms per round (budget 2 s) |

### 4.5 Node loss (R3)

| Scenario (5 runs where noted) | Result |
|---|---|
| SIGKILL the node leading both `WP_LOG` and `KV_wp_leases`: lease write outage | 5.0 / 6.1 / 7.5 s (min / median / max) |
| Same: log append outage | 5.8 / 6.3 / 11.8 s |
| Acked records missing or duplicated after failover (appends retried with the same record id) | 0 |
| Leases lost to another writer; timed-out renewals that were in fact committed | 0; 0 |
| Killed node restarted → `WP_LOG` replicas current | 2.7 / 2.9 / 3.3 s |
| 2 of 3 nodes killed: first failed renewal | 1.0 s (= client timeout) |
| 2 of 3 down: successful KV writes | 0 (no write without majority) |
| One node back: first successful renewal | 2.1–5.8 s |

## 5. Decision

1. **Adopt option 1**: JetStream is the event log and the lease store, as specified. Pin
   **nats-server ≥ 2.12** (2.12.1 tested; atomic batches and the expected-sequence subject header
   are used).
2. **Fencing uses `Nats-Expected-Last-Subject-Sequence-Subject = wp.log.<pp>.>`** with
   `Nats-Expected-Last-Subject-Sequence`, never the exact-subject check alone. Writers resolve
   `10071`/`10164` as in §4.2. M1 (single owner, `IOwnership` owns all) appends **pipelined without
   fencing headers**; T9.01 adds fencing, preferably as atomic batches (one per partition worker
   batch) with **at most 32 concurrently open batches per stream**, falling back to per-record
   fenced lanes.
3. **Appends are retried with the same `Nats-Msg-Id`** after a timeout or connection loss (dedup
   window 2 min); `AppendAsync` completes only on ack.
4. **Recovery replays with one consumer** over all partitions being recovered (whole log when the
   node recovers all 64), dispatching records to partition workers, instead of one filtered
   consumer per partition. New reader method `ReadPartitionsAsync` (PIP §5.4).
5. **Durability**: keep JetStream's default `sync_interval` (no fsync per write). In R3 an acked
   record is on a majority of nodes. On a single node, acked records survive a process crash
   (SIGKILL tests) but the last seconds may be lost on power loss/OS crash; this is accepted for M1
   and documented in DEP. `sync_interval: always` is not offered as a default (5.5k rec/s).
6. **Lease timings** change (CLU §5, §12), because a plain leader failover already blocks KV
   writes for up to 7.5 s: with TTL 10 s the local deadline (last renewal + TTL − 2 s = 8 s) and
   the 6 s quorum-loss timeout would make **every** node stop its work on a single node loss.
   New defaults: `Cluster:LeaseTtl` **15 s**, `RenewInterval` 2 s, `QuorumLossTimeout` **10 s**
   (worst measured gap 2 s + 7.5 s = 9.5 s < 13 s deadline). Failover remains < 30 s (expiry
   15 s + acquisition + restore of a ≤ 5 s tail).
7. **Replay target** in CLU §7 is restated: replay covers the tail since the last state snapshot
   (`State:SnapshotInterval` 5 s), not "1 h of log"; 1 h at 20,000 rec/s (72 M records) would take
   about 7 minutes at the measured whole-log rate.

## 6. Consequences

- PIP T1.09: writer pipelines with a bounded in-flight window (≥ 256), retries with the same id,
  `ReadPartitionsAsync` for recovery. Integration tests must not delete and immediately re-create
  a stream of the same name: during the spike the re-created stream intermittently still held the
  deleted stream's records (same sequences). Tests isolate with unique `Log:StreamPrefix` /
  `Log:SubjectPrefix` (already in PIP §5.1).
- PIP T1.10: ingest must absorb a log append outage of up to ~12 s in R3 (back-pressure through the
  bounded partition queues, PIP §8) without failing ingest callers.
- CLU T9.01/T9.02: lease and fencing rules as in §5; CLU-I tests confirm the timings on a real
  multi-host network (the spike ran all nodes on one host, 5 failover runs).
- README §4: NATS server ≥ 2.12. NATS.Net 3.3 lacks an atomic batch API; T9.01 either
  implements the headers itself (as the spike does) or upgrades the client if one appears.
- Not measured: cold-disk replay, multi-host network latency, `sync_interval` values between
  "always" and the default, one-stream-per-partition layout (option 3).
