# schemas — JSON Schemas

Document schemas (JSON Schema draft 2020-12). See [spec SCM](../Architecture/specs/SCM-schemas-and-types.md).

| File | `$id` | Documents |
|---|---|---|
| `common.schema.json` | `https://schemas.watchpoint.dev/common/1.json` | Shared definitions (ids, versions, durations, parameter schema §4.4) |
| `capability.schema.json` | `https://schemas.watchpoint.dev/capability/1.json` | Capabilities (§4) |
| `device-type.schema.json` | `https://schemas.watchpoint.dev/device-type/1.json` | Device and server types (§5) |
| `manifest.schema.json` | `https://schemas.watchpoint.dev/manifest/1.json` | Integration package manifests (§5.3) |

**Versioning.** The number in the `$id` is the format's major version; a document names it in
its `"$schema"` property. A breaking change adds a new file (e.g. `capability.v2.schema.json`
with `$id` `…/capability/2.json`); the runtime reads the current and the previous major.
Non-breaking additions keep the same `$id`.

These files are embedded into `Watchpoint.Model.Documents`, which applies them and then the
checks a schema cannot express (unique ids, `min ≤ max`, field references…).
