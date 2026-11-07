# ADR-001 raw results

Output of `tools/spikes/Watchpoint.Spikes.NatsJetStream` (nats-server 2.12.1, NATS.Net 3.3.2, 1,000,000 records), two full runs on 2026-11-07, same host (4 vCPU). See [ADR-001](ADR-001-nats-jetstream-event-log-and-leases.md) for method and decisions.

## Run A (harness commit 65e84f9)

### A.1. Single node, R1 (M1 topology), default fsync (`sync_interval` 2 min)

| Measurement | Result |
|---|---|
| R1: append latency, one record awaited | p50 0.25 ms, p95 0.39 ms, p99 0.49 ms, max 33.60 ms (n=5000) |
| R1: pipelined append, 64 in flight | 45,661 rec/s (200,000 in 4.38 s) |
| R1: pipelined append, 256 in flight | 73,514 rec/s (200,000 in 2.72 s) |
| R1: pipelined append, 1024 in flight | 85,844 rec/s (200,000 in 2.33 s) |
| Dedup: same `Nats-Msg-Id` twice | first seq 1 duplicate=False; second seq 1 duplicate=True (no new record) |
| Fencing: expected last sequence on `wp.log.17.>` (other kind) | owner appends seq 2, 3; stale writer expecting 2 → rejected: wrong last sequence: 3 (10071) |
| Fencing: expected last sequence on the exact subject only | stale writer on another kind subject is accepted → per-subject check alone does not fence a partition |
| R1: fenced append, 64 lanes (one per partition), each awaits its ack | 56,776 rec/s (200,000 in 3.52 s) |
| R1: fenced atomic batches of 10, 32 concurrent batches | 7,076 rec/s (200,000 in 28.26 s) |
| R1: fenced atomic batches of 100, 32 concurrent batches | 28,220 rec/s (200,000 in 7.09 s) |
| R1: fenced atomic batches of 100, 64 concurrent batches | failed: {"error":{"code":400,"err_code":10176,"description":"atomic publish batch is incomplete"},"stream":"WP_LOG","seq":0} |
| R1: fill replay data set, 1024 in flight | 90,978 rec/s (1,000,000 in 10.99 s) |
| Stream size | 1,000,000 records, 600 MiB (629 B/record stored) |
| Replay whole log from seq 1, warm | 174,891 rec/s (1,000,000 in 5.72 s) |
| Replay one partition (`wp.log.17.>`) from seq 1, warm | 14,871 rec/s (15,625 in 1.05 s) |
| Replay one partition from seq 1, consumer pull batch 10,000, warm | 15,417 rec/s (15,625 in 1.01 s) |
| Replay whole log from the middle, consumer pull batch 10,000, warm | 183,962 rec/s (500,001 in 2.72 s) |
| Replay one partition from the middle of the log, warm | 15,671 rec/s (7,812 in 0.50 s) |
| Replay 16 partitions (one consumer, 16 filters) from seq 1, warm | 40,246 rec/s (250,000 in 6.21 s) |
| Replay whole log from seq 1, after server restart | 197,920 rec/s (1,000,000 in 5.05 s) |
| Replay one partition (`wp.log.17.>`) from seq 1, after server restart | 16,178 rec/s (15,625 in 0.97 s) |
| Replay one partition from seq 1, consumer pull batch 10,000, after server restart | 16,519 rec/s (15,625 in 0.95 s) |
| Replay whole log from the middle, consumer pull batch 10,000, after server restart | 183,129 rec/s (500,001 in 2.73 s) |
| Replay one partition from the middle of the log, after server restart | 16,402 rec/s (7,812 in 0.48 s) |
| Replay 16 partitions (one consumer, 16 filters) from seq 1, after server restart | 38,361 rec/s (250,000 in 6.52 s) |

### A.2. Single node, R1, `sync_interval: always` (fsync every write)

| Measurement | Result |
|---|---|
| R1 fsync: append latency, one record awaited | p50 0.46 ms, p95 0.72 ms, p99 1.52 ms, max 5.08 ms (n=2000) |
| R1 fsync: pipelined append, 1024 in flight | 5,260 rec/s (200,000 in 38.02 s) |

### A.3. Three nodes, R3 (CLU topology; all nodes on one host)

| Measurement | Result |
|---|---|
| R3: append latency, one record awaited | p50 0.45 ms, p95 0.61 ms, p99 0.81 ms, max 8.60 ms (n=3000) |
| R3: pipelined append, 1024 in flight | 52,169 rec/s (200,000 in 3.83 s) |
| R3: fenced append, 64 lanes | 26,796 rec/s (100,000 in 3.73 s) |
| R3: fenced atomic batches of 100, 32 concurrent batches | 80,945 rec/s (200,000 in 2.47 s) |
| KV `Create` on an existing key | rejected: NatsKVCreateException |
| KV `Update` with a stale revision | rejected: NatsKVWrongLastRevisionException |
| Fencing token monotonic per key | create rev 1 → renew rev 2 → delete (CAS) → re-create rev 4 (revisions are bucket stream sequences: strictly increasing, also across delete/re-create) |
| CAS contention: 6 clients on 3 nodes, 500 rounds | rounds with ≠ 1 winner: create 0, take-over update 0 |
| Lease renew (CAS update) latency, client on stream leader n0 | p50 0.31 ms, p95 0.53 ms, p99 0.75 ms, max 4.79 ms (n=2000) |
| Lease renew (CAS update) latency, client on follower n1 | p50 0.47 ms, p95 0.64 ms, p99 0.78 ms, max 2.31 ms (n=2000) |
| Lease renew (CAS update) latency, client on follower n2 | p50 0.44 ms, p95 0.60 ms, p99 0.74 ms, max 5.51 ms (n=2000) |
| Renewal round of 5,000 leases, 256 CAS in flight (10 rounds) | p50 93.14 ms, p95 106.42 ms, p99 106.42 ms, max 106.42 ms (n=10) |

### A.4. Node loss: leader of `WP_LOG` and `wp_leases` SIGKILLed (R3)

| Measurement | Result |
|---|---|
| Killed node | n1 (leader of `KV_wp_leases`; `WP_LOG` leader was n1); clients connected to n2; new leaders: KV n0, log n2 |
| Failed operations after the kill | renewals 12, appends 18 |
| Lease renewals (every 100 ms, 1 s timeout): longest gap between successes | 4,323.27 ms |
| Lease renewals: timed out but actually committed (resolved by re-reading the key) | 0 |
| Lease renewals: lost to another writer (must be 0) | 0 |
| Log appends (sequential, 1 s timeout, retry same `Nats-Msg-Id`): longest gap | 5,297.13 ms |
| Log appends: retries | 18 |
| Acknowledged records after failover | 2,175 acked, 0 missing, 0 duplicated record ids in the stream |
| Restarted node: process start → `WP_LOG` replicas all current | 3,233.40 ms |
| 5 runs: lease write outage (min / median / max) | 4,323.27 ms / 5,381.57 ms / 7,492.62 ms |
| 5 runs: log append outage (min / median / max) | 4,533.10 ms / 6,548.14 ms / 7,308.58 ms |
| 5 runs: restarted node catch-up (min / median / max) | 2,216.90 ms / 3,116.21 ms / 3,441.79 ms |
| 5 runs: acked records missing or duplicated / leases lost | 0 / 0 |

### A.5. Quorum loss: 2 of 3 nodes SIGKILLed (R3)

| Measurement | Result |
|---|---|
| 2 of 3 nodes killed: time to first failed renewal | 275.64 ms |
| Successful renewals while 2 of 3 down (from +200 ms, must be 0) | 0 |
| One node restarted: process start → first successful renewal | 5,381.24 ms |
| Lease renewals lost to another writer (must be 0) | 0 |

Harness note: stream delete + immediate re-create returned the old records 1 time(s) (retried).

### A.Unexpected fencing rejections (no competing writer)

- partition 58, record 797, expected 50670: 'wrong last sequence' (10164); last on filter = seq 50670 subject wp.log.58.property; stream 50878 msgs, last seq 50878
- total: 1

## Run B (harness commit e7d398f, before the stale re-create detection: its first two 'unexpected rejections' are the deleted stream's records, see ADR-001 §6)

### B.1. Single node, R1 (M1 topology), default fsync (`sync_interval` 2 min)

| Measurement | Result |
|---|---|
| R1: append latency, one record awaited | p50 0.22 ms, p95 0.38 ms, p99 0.55 ms, max 28.54 ms (n=5000) |
| R1: pipelined append, 64 in flight | 48,371 rec/s (200,000 in 4.13 s) |
| R1: pipelined append, 256 in flight | 78,580 rec/s (200,000 in 2.55 s) |
| R1: pipelined append, 1024 in flight | 92,761 rec/s (200,000 in 2.16 s) |
| Dedup: same `Nats-Msg-Id` twice | first seq 1 duplicate=False; second seq 1 duplicate=True (no new record) |
| Fencing: expected last sequence on `wp.log.17.>` (other kind) | owner appends seq 2, 3; stale writer expecting 2 → rejected: wrong last sequence: 3 (10071) |
| Fencing: expected last sequence on the exact subject only | stale writer on another kind subject is accepted → per-subject check alone does not fence a partition |
| R1: fenced append, 64 lanes (one per partition), each awaits its ack | 55,027 rec/s (200,000 in 3.63 s) |
| R1: fenced atomic batches of 10, 32 concurrent batches | 8,103 rec/s (200,000 in 24.68 s) |
| R1: fenced atomic batches of 100, 32 concurrent batches | 34,861 rec/s (200,000 in 5.74 s) |
| R1: fenced atomic batches of 100, 64 concurrent batches | failed: {"error":{"code":400,"err_code":10176,"description":"atomic publish batch is incomplete"},"stream":"WP_LOG","seq":0} |
| R1: fill replay data set, 1024 in flight | 87,851 rec/s (1,000,000 in 11.38 s) |
| Stream size | 1,000,000 records, 600 MiB (629 B/record stored) |
| Replay whole log from seq 1, warm | 175,821 rec/s (1,000,000 in 5.69 s) |
| Replay one partition (`wp.log.17.>`) from seq 1, warm | 16,685 rec/s (15,625 in 0.94 s) |
| Replay one partition from seq 1, consumer pull batch 10,000, warm | 16,617 rec/s (15,625 in 0.94 s) |
| Replay whole log from the middle, consumer pull batch 10,000, warm | 187,808 rec/s (500,001 in 2.66 s) |
| Replay one partition from the middle of the log, warm | 14,219 rec/s (7,812 in 0.55 s) |
| Replay 16 partitions (one consumer, 16 filters) from seq 1, warm | 39,225 rec/s (250,000 in 6.37 s) |
| Replay whole log from seq 1, after server restart | 177,979 rec/s (1,000,000 in 5.62 s) |
| Replay one partition (`wp.log.17.>`) from seq 1, after server restart | 15,835 rec/s (15,625 in 0.99 s) |
| Replay one partition from seq 1, consumer pull batch 10,000, after server restart | 15,658 rec/s (15,625 in 1.00 s) |
| Replay whole log from the middle, consumer pull batch 10,000, after server restart | 202,304 rec/s (500,001 in 2.47 s) |
| Replay one partition from the middle of the log, after server restart | 15,559 rec/s (7,812 in 0.50 s) |
| Replay 16 partitions (one consumer, 16 filters) from seq 1, after server restart | 44,861 rec/s (250,000 in 5.57 s) |

### B.2. Single node, R1, `sync_interval: always` (fsync every write)

| Measurement | Result |
|---|---|
| R1 fsync: append latency, one record awaited | p50 0.45 ms, p95 0.67 ms, p99 0.93 ms, max 3.01 ms (n=2000) |
| R1 fsync: pipelined append, 1024 in flight | 5,550 rec/s (200,000 in 36.04 s) |

### B.3. Three nodes, R3 (CLU topology; all nodes on one host)

| Measurement | Result |
|---|---|
| R3: append latency, one record awaited | p50 0.44 ms, p95 0.67 ms, p99 0.88 ms, max 3.67 ms (n=3000) |
| R3: pipelined append, 1024 in flight | 51,736 rec/s (200,000 in 3.87 s) |
| R3: fenced append, 64 lanes | 32,343 rec/s (100,000 in 3.09 s) |
| R3: fenced atomic batches of 100, 32 concurrent batches | 65,556 rec/s (200,000 in 3.05 s) |
| KV `Create` on an existing key | rejected: NatsKVCreateException |
| KV `Update` with a stale revision | rejected: NatsKVWrongLastRevisionException |
| Fencing token monotonic per key | create rev 1 → renew rev 2 → delete (CAS) → re-create rev 4 (revisions are bucket stream sequences: strictly increasing, also across delete/re-create) |
| CAS contention: 6 clients on 3 nodes, 500 rounds | rounds with ≠ 1 winner: create 0, take-over update 0 |
| Lease renew (CAS update) latency, client on follower n0 | p50 0.48 ms, p95 0.67 ms, p99 0.79 ms, max 4.86 ms (n=2000) |
| Lease renew (CAS update) latency, client on stream leader n1 | p50 0.37 ms, p95 0.56 ms, p99 0.74 ms, max 10.67 ms (n=2000) |
| Lease renew (CAS update) latency, client on follower n2 | p50 0.52 ms, p95 0.71 ms, p99 0.86 ms, max 7.76 ms (n=2000) |
| Renewal round of 5,000 leases, 256 CAS in flight (10 rounds) | p50 106.72 ms, p95 155.53 ms, p99 155.53 ms, max 155.53 ms (n=10) |

### B.4. Node loss: leader of `WP_LOG` and `wp_leases` SIGKILLed (R3)

| Measurement | Result |
|---|---|
| Killed node | n0 (leader of `KV_wp_leases`; `WP_LOG` leader was n0); clients connected to n1; new leaders: KV n1, log n1 |
| Failed operations after the kill | renewals 17, appends 31 |
| Lease renewals (every 100 ms, 1 s timeout): longest gap between successes | 6,084.24 ms |
| Lease renewals: timed out but actually committed (resolved by re-reading the key) | 0 |
| Lease renewals: lost to another writer (must be 0) | 0 |
| Log appends (sequential, 1 s timeout, retry same `Nats-Msg-Id`): longest gap | 8,563.74 ms |
| Log appends: retries | 31 |
| Acknowledged records after failover | 1,358 acked, 0 missing, 0 duplicated record ids in the stream |
| Restarted node: process start → `WP_LOG` replicas all current | 3,330.20 ms |
| 5 runs: lease write outage (min / median / max) | 5,028.41 ms / 6,084.24 ms / 7,492.67 ms |
| 5 runs: log append outage (min / median / max) | 5,793.44 ms / 6,295.85 ms / 11,842.05 ms |
| 5 runs: restarted node catch-up (min / median / max) | 2,719.16 ms / 2,911.24 ms / 3,330.86 ms |
| 5 runs: acked records missing or duplicated / leases lost | 0 / 0 |

### B.5. Quorum loss: 2 of 3 nodes SIGKILLed (R3)

| Measurement | Result |
|---|---|
| 2 of 3 nodes killed: time to first failed renewal | 1,021.30 ms |
| Successful renewals while 2 of 3 down (from +200 ms, must be 0) | 0 |
| One node restarted: process start → first successful renewal | 5,772.39 ms |
| Lease renewals lost to another writer (must be 0) | 0 |

### B.Unexpected fencing rejections (no competing writer)

- partition 3, record 0, expected 0: 'wrong last sequence: 1' (10071); last on filter = seq 1 subject wp.log.03.property; stream 316 msgs, last seq 316
- partition 17, record 0, expected 0: 'wrong last sequence: 4' (10071); last on filter = seq 4 subject wp.log.17.health; stream 317 msgs, last seq 317
- partition 26, record 463, expected 30028: 'wrong last sequence' (10164); last on filter = seq 30028 subject wp.log.26.property; stream 30131 msgs, last seq 30131
- total: 3
