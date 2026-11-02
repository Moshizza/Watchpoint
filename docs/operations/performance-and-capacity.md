# Performance and Capacity

> Sites range from 50 to 10,000+ devices. The design must not depend on what is "typical":
> limits are measured, published and monitored, and a site grows by adding nodes.

## 1. Load model

What actually loads a system is less the number of devices than the **rate of changes** and
the **number of active clients**:

| Driver | Typical | Peak (design target) |
|---|---|---|
| Property changes per device per hour | 1–20 | burst: every device changing within seconds (power cut, panel restart) |
| Events per second (site of 10,000 devices) | 10–50 | 2,000 for 60 s (mass event: fire alarm, network restoration) |
| Commands per second | < 1 | 200 (group lockdown of 1,000 doors) |
| Alarms raised per minute | 0–10 | 1,000 (alarm flood) |
| Concurrent clients | 2–20 | 200 (with front nodes) |
| Live video streams | 4–50 | depends on media nodes |

## 2. Targets for one node (to be validated by load tests)

Reference machine: 8 vCPU, 32 GB RAM, NVMe SSD, PostgreSQL on the same machine.

| Metric | Target |
|---|---|
| Devices supervised | 25,000 |
| Sustained observations ingested | 5,000/s |
| Burst observations | 20,000/s for 60 s without loss (back-pressure into the log) |
| End-to-end latency, field observation → operator screen (p95, normal load) | < 300 ms (excluding vendor polling interval) |
| Alarm raise → console (p95 under alarm flood of 1,000/min) | < 1 s |
| Command accepted → dispatched (p95) | < 100 ms |
| Concurrent client sessions | 50 |
| Failover time (redundant pair) | < 30 s |
| Recovery after crash (state rebuild) | < 60 s for 25,000 devices |

Scale-out: additional `integration` and `pipeline` nodes add roughly linear capacity;
additional `front` nodes add client capacity; `media` nodes add video capacity.

## 3. How the design achieves it

- Partitioned, lock-free pipeline (see [Core runtime](../modules/core-runtime.md)).
- Coalescing of state updates under load; events never dropped.
- History written in batches by a separate consumer.
- Clients subscribe only to what they display.
- Video outside the core.
- Rule evaluation via indexes, not scans.

## 4. Load testing programme

- **Simulators** for every integration and a generic **device simulator** (configurable
  number of servers/devices, change rates, bursts, failures).
- Scenarios: steady state at target; bursts; alarm flood; mass command; client storm
  (200 clients reconnecting); node failure under load; database failover under load; WAN
  outage and resume for federation.
- Run in CI nightly on reference hardware; results published per release with the
  **capacity sheet** given to sales and integrators (devices per node, clients per node,
  recommended hardware tiers).

## 5. Sizing guide (initial, to be replaced by measurements)

| Site size | Recommended deployment |
|---|---|
| ≤ 1,000 devices, ≤ 10 clients | 1 node, 4 vCPU, 16 GB RAM (+ standby node if redundancy) |
| ≤ 10,000 devices, ≤ 30 clients | 1 node 8 vCPU / 32 GB, or redundant pair + witness |
| ≤ 50,000 devices or > 30 clients | Cluster: 2 front + 2–4 integration/pipeline nodes + dedicated database server |
| Multi-site | One cluster per site sized as above + central site sized for its own clients and federated load |

## 6. Capacity monitoring

The health dashboard shows utilisation against the measured limits (ingest rate, queue
lengths, CPU per role, database load, sessions) and recommends actions ("integration load
85 % of node capacity — consider adding an integration node").
