# Integration Worked Examples

These documents test the [integration design](../modules/integrations.md) and the
[device model](../modules/servers-and-devices.md) against real systems, one per category:

| Category | System | Document |
|---|---|---|
| Access control | Salto Space | [salto-space.md](salto-space.md) |
| Video | Milestone XProtect | [milestone-xprotect.md](milestone-xprotect.md) |
| Intrusion | Vanderbilt SPC | [vanderbilt-spc.md](vanderbilt-spc.md) |

Second intrusion integration planned: Honeywell Galaxy (older serial/gateway interfaces —
a good test of the design on legacy systems), then Aritech ATS.

> **Important**: vendor protocols and APIs mentioned here must be confirmed against the
> vendors' official integration documentation and partner agreements before development.
> These documents describe Watchpoint's *mapping and behaviour*, not the vendors' protocols
> in detail. Any detail learned from other products' implementations must not be reused;
> build from vendor documentation and our own tests.
