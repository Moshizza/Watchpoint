# Spike T0.11 — measurements

- Date (UTC): 2026-11-07 16:13
- OS: Ubuntu 24.04.5 LTS (X64); .NET 10.0.14; 4 logical CPUs
- CPU: Intel(R) Xeon(R) Processor
- Counts: burst 2,000,000 reports, paced 3 s per rate, 20,000 commands, 10 host starts

## 1. Load, start and memory

| Metric | Value |
|---|---|
| In-process: load context + connector load (first, cold) | 3.482 ms |
| In-process: node RSS growth after loading the connector | 0.340 MB |
| Isolated: process start → Hello, median / max of 10 | 153 / 240 ms |
| Isolated: connector load inside the host (median) | 5.190 ms |
| Isolated: idle host RSS (ICU globalization) | 46.3 MB |
| Isolated: idle host RSS (invariant globalization) | 43.5 MB |

## 2. Observation throughput (burst, connector reports as fast as it can)

| Mode | reports/s (median of 3) |
|---|---|
| In-process | 2,156,617 |
| Isolated, batching `eager` | 1,984,679 |
| Isolated, batching `timer` | 2,821,784 |

## 3. Latency report → ingest (paced load)

| Rate | Mode | p50 ms | p99 ms | p99.9 ms | max ms |
|---|---|---|---|---|---|
| 1,000/s | In-process | 0.048 | 0.124 | 0.255 | 0.485 |
| 1,000/s | Isolated, `eager` | 0.137 | 2.935 | 6.761 | 7.759 |
| 1,000/s | Isolated, `timer` | 26.9 | 52.9 | 56.2 | 58.2 |
| 10,000/s | In-process | 0.024 | 0.101 | 5.302 | 7.199 |
| 10,000/s | Isolated, `eager` | 0.048 | 3.263 | 5.023 | 6.822 |
| 10,000/s | Isolated, `timer` | 25.9 | 50.4 | 53.0 | 54.2 |
| 50,000/s | In-process | 0.002 | 0.075 | 6.629 | 9.568 |
| 50,000/s | Isolated, `eager` | 0.044 | 3.667 | 6.499 | 8.002 |
| 50,000/s | Isolated, `timer` | 5.513 | 10.5 | 13.6 | 15.1 |

## 4. Command round-trip (sequential, node → connector → node)

| Mode | p50 ms | p99 ms | p99.9 ms | max ms |
|---|---|---|---|---|
| In-process | 0.000 | 0.000 | 0.000 | 0.071 |
| Isolated | 0.103 | 0.258 | 0.762 | 20.5 |

## 5. Connector faults

| Fault | In-process (node process) | Isolated: node detects link loss after | Isolated: node afterwards |
|---|---|---|---|
| BackgroundThreadException | **node process dies** (exit code 134) | 29.4 ms | keeps running (in-process work unaffected); new host Ready in 137 ms |
| StackOverflow | **node process dies** (exit code 134) | 25.5 ms | keeps running (in-process work unaffected); new host Ready in 141 ms |
| FailFast | **node process dies** (exit code 134) | 16.8 ms | keeps running (in-process work unaffected); new host Ready in 142 ms |
| Hang | survives, one core spinning until the node restarts (threads cannot be aborted) | not detected by the link: heartbeat age 0.035 s, commands answered: True | host killed by the node, link loss seen after 9.847 ms |

## 6. Unloading the in-process load context

| Case | Collected | GC rounds | Time ms |
|---|---|---|---|
| Clean connector | yes | 2 | 36.0 |
| Connector subscribed to a static event of a shared assembly | **no** | 20 | 18.9 |

