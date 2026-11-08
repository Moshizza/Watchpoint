# types — standard type library v0

Capabilities, device/server types, symbols and translations embedded into the model module.
See [spec SCM §6](../Architecture/specs/SCM-schemas-and-types.md#6-standard-type-library-v0-types).

```
capabilities/<capId>.json        15 capabilities (health, doorPosition, lockable, …)
device-types/<typeId>.json       8 types (generic.server, ac.door, ac.reader, …)
symbols/<typeId>/<state>.svg     one SVG per overall state + default.svg (48×48, colours from CSS variables)
i18n/en.json, fr.json, ar.json   derived translation keys (SCM §3.2); fr/ar machine-assisted, needs-review
```

Check every change with:

```bash
dotnet run --project tools/wptypes -- validate types --strict-i18n
```

Alarm rule templates referenced by `defaultAlarmRules` are reported as warnings until ALM exists.
