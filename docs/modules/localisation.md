# Localisation (Languages, Right-to-Left, Time Zones)

## 1. Languages

| Wave | Languages |
|---|---|
| v1 | English (`en`), French (`fr`), Arabic (`ar`, right-to-left) |
| Next (recommended for target markets) | Spanish (`es`), German (`de`), Turkish (`tr`) |
| Later | Chinese simplified (`zh-Hans`), Portuguese (`pt`), Italian (`it`), others on demand |

Designing for these from day one: right-to-left (covers Hebrew, Persian, Urdu later), CJK
fonts and line breaking, long words (German), special casing (Turkish dotted/dotless i).

## 2. Principles

- **No user-visible text in code or from the server as plain strings.** Every message is a
  translation key with parameters (ICU MessageFormat: plurals, gender, select, number and
  date formats).
- Clients: i18next + ICU; server: .NET resources for its few rendered outputs (reports,
  e-mails) using the same keys.
- Integration packages ship their own translations (device types, properties, values,
  commands, events, settings forms, errors) for every v1 language — a release requirement.
- User content (device names, location names, alarm rule names, procedures, synoptic labels)
  can have **optional translations**; the display falls back to the default name.
- Language per user (and per notification recipient); the site has a default.

## 3. Right-to-left (Arabic)

- Layout direction set on the root (`dir="rtl"`); UI built with **logical CSS properties**
  (Tailwind logical utilities: `ms-*`, `me-*`, `ps-*`, `start-*`) so layouts mirror
  automatically.
- Components from Radix/shadcn support direction; tables, trees, timelines and the alarm
  console are tested mirrored.
- Icons that imply direction (arrows, back/forward) mirror; others (play, clock, logos) do
  not.
- Mixed content (Arabic text with Latin device names and numbers) uses Unicode bidi
  isolation around inserted values.
- **Synoptics and maps are not mirrored** (a floor plan is geographic), only their panels and
  labels.
- Charts: axes and legends adapted for RTL.
- Fonts: Noto Sans + Noto Sans Arabic (SIL Open Font License), bundled for offline sites.
- Digits: Western Arabic digits by default (common in security operations), Eastern Arabic
  digits as a user option.

## 4. Dates, times, numbers, units

- Storage in UTC everywhere.
- Display in the **user's time zone** or the **source site's time zone** (switchable; alarm
  lists in multi-site federations show the site's local time with a marker).
- Each site has an IANA time zone; integrated servers declare their own time zone (panels
  often run on local time) for correct conversion.
- Calendar: Gregorian by default; Hijri date display as an option for Arabic users
  (display only; schedules remain Gregorian with holiday sets per country).
- Numbers, decimals, thousands separators, units and temperature scales per locale and user
  preference.

## 5. Translation workflow

- Source language: English. Keys extracted automatically from code (front-end and packages).
- Translators work in **Weblate** (self-hosted, free) with screenshots and context.
- CI checks: missing keys, unused keys, ICU syntax, placeholders consistency, maximum length
  hints.
- **Pseudo-localisation** build (accents, +40 % length, RTL pseudo-locale) used in automated
  UI tests to catch hard-coded strings and layout overflows.
