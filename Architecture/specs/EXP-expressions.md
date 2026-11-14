# EXP — Expressions (CEL Subset)

| | |
|---|---|
| Spec id | EXP |
| Projects | `src/service/Watchpoint.Expressions` (C#), `src/ui/packages/expressions` (TypeScript), `tests/expressions/vectors/` (shared conformance vectors) |
| Milestone | M2 |
| Tasks | T2.06 (§4–§7, C#), T2.07 (§8–§9, TypeScript + vectors) |
| Design docs | [Technology stack §2.6](../../docs/architecture/technology-stack.md#26-why-cel-for-expressions) |

## 1. Purpose and scope

A small, safe expression language used wherever configuration needs a condition or a computed
value without code: alarm rule conditions (ALM), synoptic bindings (SYN), automation conditions
(AUT), filters. Syntax is a **subset of Google's CEL**. Expressions cannot loop, cannot have side
effects, have bounded cost, and are **type-checked against the device model** at edit time so
errors are explained in plain language.

### 1.1 Decision: two implementations, one test corpus

The server evaluates expressions on the hot path (alarm conditions on every relevant record), so
it uses a native **C#** evaluator. Clients (synoptics, editors) use a **TypeScript** evaluator.
Both implement the same grammar, type rules and functions, and must pass the same **conformance
vectors** (JSON files shared by both test suites). This replaces the earlier idea of running a
single TypeScript evaluator inside V8 on the server (no V8 dependency before scripting, M8, and
no interop cost per evaluation).

## 2. Dependencies

C#: K, MOD (environment declarations from types). No third-party parser. TypeScript:
`@watchpoint/model`.

## 3. Layout

```
Watchpoint.Expressions/
├── Contracts/IExpressionCompiler.cs, CompiledExpression.cs, ExpressionEnvironment.cs, Diagnostic.cs, IEvaluationContext.cs
├── Internal/Lexer.cs, Parser.cs (Pratt), Ast.cs, TypeChecker.cs, Evaluator.cs, Functions/*.cs, Suggestions.cs
└── ExpressionsModule.cs
src/ui/packages/expressions/src/  lexer.ts, parser.ts, ast.ts, checker.ts, evaluator.ts, functions.ts, completion.ts
tests/expressions/vectors/         *.json (shared)
```

## 4. Language

### 4.1 Lexical

- Identifiers `[A-Za-z_][A-Za-z0-9_]*`; reserved: `true false null in`.
- Literals: integers (`42`, `-7`), decimals (`21.5`), strings (`"text"` or `'text'`, escapes `\" \' \\ \n \t \uXXXX`), booleans, `null`.
- Comments: none. Whitespace insignificant. Max length 2,000 characters.

### 4.2 Grammar (precedence from low to high)

```
expr        := conditional
conditional := or ( "?" expr ":" expr )?
or          := and ( "||" and )*
and         := relation ( "&&" relation )*
relation    := addition ( ( "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" ) addition )?
addition    := multiplication ( ( "+" | "-" ) multiplication )*
multiplication := unary ( ( "*" | "/" | "%" ) unary )*
unary       := ( "!" | "-" )* member
member      := primary ( "." IDENT | "." IDENT "(" args? ")" | "[" expr "]" )*
primary     := literal | IDENT | IDENT "(" args? ")" | "(" expr ")" | "[" args? "]"
args        := expr ( "," expr )*
```

Relations are non-associative (`a < b < c` is a syntax error with a hint).

### 4.3 Types and values

| Type | Values | From `WpValue` |
|---|---|---|
| `bool` | true/false | Bool |
| `int` | 64-bit | Int |
| `decimal` | 28 digits (C# `decimal`, TS `decimal.js-light` MIT) | Decimal |
| `string` | Unicode | String, Enum (enum codes are strings) |
| `timestamp` | UTC instant | Timestamp |
| `duration` | span | Duration |
| `list<T>` | ≤ 1,000 items | List |
| `null` | absence | Null or unknown value |
| `object` (environment objects only: `device`, `event`) | member access only | — |

Rules: `int` and `decimal` mix in arithmetic and comparison (int promoted); no implicit
string↔number conversion; `==`/`!=` between different types is a **type error at check time**
(except with `null`); comparisons with `null` other than `==`/`!=` evaluate to `false`
(runtime) and produce a check-time **warning** when the operand can be null; `&&`/`||` short-circuit
and treat runtime errors in the unevaluated branch as irrelevant (CEL semantics: `false &&
error = false`).

### 4.4 Functions

| Function | Signature | Notes |
|---|---|---|
| `size(x)` / `x.size()` | string/list → int | |
| `s.contains(t)`, `s.startsWith(t)`, `s.endsWith(t)` | string → bool | Ordinal, case-sensitive |
| `s.lower()`, `s.upper()` | string → string | Invariant culture |
| `s.matches(re)` | string → bool | Regex compiled once at check time; .NET `RegexOptions.NonBacktracking` / JS `u` flag; patterns limited to a portable subset (no backreferences, no lookaround) — validated at check time |
| `x in list` | T, list<T> → bool | |
| `has(device.capability)` / `has(device.capability.property)` | → bool | True if the type has it and (for properties) a value is known |
| `int(x)`, `decimal(x)`, `string(x)` | conversions | `int(21.7) = 21` (truncation); `string(enum)` identity |
| `duration("PT10S")` | string literal → duration | ISO 8601; **literal argument only** (checked) |
| `timestamp("2026-01-01T00:00:00Z")` | literal → timestamp | |
| `now` | timestamp (variable) | Evaluation time (UTC) |
| `ago(d)` | duration → timestamp | `now - d` |
| `t.hour(tz?)`, `t.dayOfWeek(tz?)`, `t.minute(tz?)` | timestamp → int | `tz` IANA string literal; default site time zone |
| `inSchedule("calendarKey")` | → bool | Calendar from SCH (key literal, checked to exist) |
| `abs(x)`, `min(a, b)`, `max(a, b)`, `round(x, digits)` | numbers | |

No user-defined functions, no macros (`all`, `exists`, `map`, `filter` are **not** supported in
v1 — they are the main cost risk; may be added later with bounded list sizes).

### 4.5 Environments

An `ExpressionEnvironment` declares the variables available in a context, with types derived
from the device model:

| Context | Variables |
|---|---|
| Alarm rule on devices (ALM) | `device` (the evaluated device), `event` (for event triggers), `now` |
| Synoptic binding (SYN) | `device` (bound device), `refs` (map of named device references declared in the element: `refs.mainDoor.doorPosition.position`), `now` |
| Automation condition (AUT) | `trigger` (trigger data), `devices` via `refs`, `vars` (automation variables, declared types), `now` |

`device` object members:

| Member | Type |
|---|---|
| `device.<capabilityId>.<propertyId>` | property type (enum → string; unknown → null) |
| `device.health.connectivity`, `device.health.faults` | string, list<string> |
| `device.overall` | string (overall state) |
| `device.attributes.<key>` | attribute type |
| `device.tags` | list<string> (tag keys) |
| `device.location` | list<string> (location ids root→leaf) — use `"<locationId>" in device.location` |
| `device.type` | string (type id); `device.isA("ac.door")` → bool |
| `device.name` | string (default display name) |
| `device.maintenance`, `device.enabled` | bool |
| `device.quality("<cap>.<prop>")` | string (`good`, `uncertain`, `bad`, `stale`) |
| `device.changedAgo("<cap>.<prop>")` | duration since last change (null if never) |

`event` members: `event.kind` (string), `event.severity` (string), `event.payload.<field>`
(typed from the event definition), `event.sourceTime` (timestamp).

When an expression is checked for a **set of device types** (e.g. a rule scoped to "all doors"),
`device` has the members common to all those types; using a member missing in some types is an
error unless guarded by `has(...)` (which makes it a warning-free optional access).

## 5. Compiler contract (C#)

```csharp
public interface IExpressionCompiler
{
    CompileResult Compile(string source, ExpressionEnvironment environment, ExpectedType expected /* Bool for conditions, Any for values */);
}

public sealed record CompileResult(CompiledExpression? Expression, IReadOnlyList<Diagnostic> Diagnostics)
{ public bool Success => Expression is not null && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error); }

public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, IReadOnlyDictionary<string, object?> Args,
    int Start, int Length /* UTF-16 offsets in source */);

public sealed class CompiledExpression
{
    public string Source { get; }
    public ExprType ResultType { get; }
    public IReadOnlyList<PropertyPath> ReferencedProperties { get; }   // for indexing (ALM) and subscriptions (SYN)
    public IReadOnlyList<string> ReferencedRefs { get; }
    public bool UsesNow { get; }                                        // time-dependent → re-evaluated on ticks
    public IReadOnlyList<string> ReferencedCalendars { get; }
    public EvalResult Evaluate(IEvaluationContext context);
    public string ToAstJson();                                           // canonical AST (same as TS)
}

public interface IEvaluationContext
{
    DateTimeOffset Now { get; }
    bool TryGetVariable(string name, out IExprObject value);
    bool InSchedule(string calendarKey, DateTimeOffset at);
    SiteTimeZone SiteTimeZone { get; }
}
public readonly record struct EvalResult(WpValue Value, string? ErrorCode)   // ErrorCode: "expr.eval.division_by_zero", "expr.eval.limit_exceeded"…
{ public bool AsCondition() => ErrorCode is null && Value.Kind == WpValueKind.Bool && Value.AsBool(); }
```

Limits (checked at compile and enforced at evaluation):

| Limit | Value | Diagnostic |
|---|---|---|
| Source length | 2,000 chars | `expr.too_long` |
| AST nodes | 500 | `expr.too_complex` |
| Nesting depth | 32 | `expr.too_deep` |
| Evaluation steps | 10,000 | runtime `expr.eval.limit_exceeded` |
| List literal / list values | 1,000 items | `expr.list.too_large` |
| Regex evaluation | 10 ms per match | runtime `expr.eval.regex_timeout` |

Performance target: a typical condition (5–10 nodes) evaluates in < 1 µs (C#, no allocations
on the hot path except result boxing avoided via `WpValue` struct). The evaluator is a struct and
variable member paths (`device.a.b`) are resolved once at parse time, so an evaluation allocates
nothing. Unit tests check the allocation-free part (EXP-U-040); the < 1 µs latency is a benchmark
target, not a unit-test assertion (TST §4.7: unit tests never measure wall-clock time).

## 6. Diagnostics (plain language)

| Code | Args | English message (en bundle `errors.expr.*`) |
|---|---|---|
| `expr.syntax.unexpected_token` | `found`, `expected` | "Unexpected `{found}`. Expected {expected}." |
| `expr.syntax.unterminated_string` | — | "A text value is not closed: add a closing quote." |
| `expr.syntax.chained_comparison` | — | "Comparisons cannot be chained. Use `a < b && b < c`." |
| `expr.member.unknown` | `name`, `on`, `suggestions` | "`{on}` has no `{name}`. Did you mean {suggestions}?" |
| `expr.member.not_common` | `name`, `types` | "Not every device in this rule has `{name}` (missing on {types}). Wrap it in `has(...)`." |
| `expr.type.mismatch` | `op`, `left`, `right` | "Cannot compare {left} with {right}. Use a value of the same kind, for example a number." |
| `expr.enum.unknown_value` | `value`, `allowed` | "`{value}` is not a possible value. Possible values: {allowed}." |
| `expr.function.unknown` | `name`, `suggestions` | |
| `expr.function.arguments` | `name`, `expected` | |
| `expr.calendar.unknown` | `key` | "There is no calendar named `{key}`." |
| `expr.result.not_bool` | `actual` | "This condition must be true or false, but it gives {actual}." |
| `expr.null.comparison` (warning) | `name` | "`{name}` may be unknown; the condition will be false while it is unknown." |
| `expr.regex.unsupported` | `feature` | |

Enum comparisons are checked against the property's enum codes
(`device.doorPosition.position == "opened"` → `expr.enum.unknown_value` with the list).
Suggestions use Damerau-Levenshtein distance ≤ 2 on member names and also match translated
labels (user typed "porte" → suggests `doorPosition`).

## 7. Server usage rules

- Compiled expressions are cached by `(source, environment hash)`.
- Modules evaluate with `EvalResult.AsCondition()`; runtime errors count as `false` and are
  counted per rule (`watchpoint.expressions.errors` by `owner`) and logged at most once per
  minute per owner (event id 2550).
- Expressions referencing `now` or calendars are re-evaluated by their owner on `OnTick`.

## 8. TypeScript implementation (T2.07)

- Same grammar, AST (identical JSON), checker, diagnostics codes and evaluator semantics.
- Decimals via `decimal.js-light`; timestamps as `Temporal`-free numeric epoch ms + helpers;
  time zones via `Intl.DateTimeFormat` (IANA).
- Editor services (for Monaco, used by UIF and SYN):

```ts
export function compile(source: string, env: ExpressionEnvironment, expected: "bool" | "any"): CompileResult;
export function complete(source: string, offset: number, env: ExpressionEnvironment, t: TFunction): CompletionItem[];
export function hover(source: string, offset: number, env: ExpressionEnvironment, t: TFunction): HoverInfo | undefined;
export function describe(compiled: CompiledExpression, t: TFunction): string;   // plain-language rendering: "Door position is Open and Lock state is Unlocked"
```

- Completion offers members (with translated labels and types), enum values after `==` on enum
  properties (`"open"` with label "Open"), functions with signatures, calendar keys.

## 9. Conformance vectors (shared)

`tests/expressions/vectors/*.json`, each file an array of cases:

```json
{ "id": "EXPV-0042",
  "env": "door",                                     // named environment fixture (vectors/environments/door.json)
  "source": "device.doorPosition.position == \"forced\" && device.lockable.lockState == \"locked\"",
  "expected": "bool",
  "context": { "device": { "doorPosition.position": {"k":"enum","v":"forced"}, "lockable.lockState": {"k":"enum","v":"locked"} }, "now": "2026-01-15T09:00:00.000Z" },
  "result": { "k": "bool", "v": true } }
```

or with `"diagnostics": [{ "code": "expr.type.mismatch", "start": 0, "length": 26 }]` or
`"evalError": "expr.eval.division_by_zero"`. Target: ≥ 400 vectors covering every grammar rule,
operator, type rule, function, limit and diagnostic. Both test suites load all vectors; a
vector failing in one implementation fails CI (`EXP-C-001`).

## 10. Tests

| Id | Layer | Test |
|---|---|---|
| EXP-U-001…010 | U | Lexer: literals, escapes, identifiers, reserved words, length limit, unterminated strings, positions |
| EXP-U-011…020 | U | Parser: precedence of every operator level, member/index/call chains, list literals, conditional, chained comparison error, depth and node limits |
| EXP-U-021…030 | U | Checker: environment members from MOD types (door, area, analog), enum value validation, int/decimal mixing, null warnings, not-common members across types with `has` guard, function signatures, calendars, regex subset, result type bool |
| EXP-U-031…040 | U | Evaluator: arithmetic incl. decimal precision, comparisons, `in`, string functions, conversions, time functions with time zones and DST, `ago`, `inSchedule` via fake calendar, short-circuit with errors, step limit, regex timeout, unknown values as null; EXP-U-040 allocation-free hot path: after 1,000 warm-up evaluations of `device.doorPosition.position == "open"`, 10,000 evaluations allocate < 10,000 bytes in total on the evaluating thread (`GC.GetAllocatedBytesForCurrentThread`; any per-evaluation object would be ≥ 240,000 bytes, the margin absorbs runtime JIT noise) and all return true. No wall-clock assertion (§5, TST §4.7) |
| EXP-U-041…050 | U | TypeScript lexer/parser/checker parity tests (same cases as C#) and AST JSON identical |
| EXP-U-051…060 | U | TypeScript evaluator, completion (members, enum values, functions), hover, plain-language `describe` in en/fr/ar |
| EXP-C-001 | C | All conformance vectors pass in both implementations |

(Each range contains individual test cases with their own ids `EXP-U-0nn` in the test code; the
range rows above list what each group must cover.)

## 11. Tasks

| Task | Sections | Tests |
|---|---|---|
| T2.06 | §4–§7 | EXP-U-001…EXP-U-040 |
| T2.07 | §8, §9 | EXP-U-041…EXP-U-060, EXP-C-001 |
