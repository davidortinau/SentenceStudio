# CoreSync — Suspected Defects (Internal Working Document)

> **Status: INTERNAL ONLY — DO NOT FILE UPSTREAM YET.**
>
> This document records three suspected defects in the CoreSync library
> (versions 0.1.122 and 0.1.127, decompiled and inspected) that we believe
> together explain the `VocabularyProgress` corruption event observed on
> Captain's Mac Catalyst install (1745 rows where the column NAME was stored
> as the column VALUE for `UserDeclaredAt`, `VerificationState`, and
> `IsUserDeclared`).
>
> Per Captain's call (post-Round 2 troubleshooting), we will **not** open
> upstream issues until each defect below is independently verified by a
> minimal local repro. If/when we do file, this doc becomes the issue body
> draft.
>
> The originator of the corruption is still unknown — none of the three
> defects below alone produce a "column name as value" write. They explain
> *propagation and silent acceptance*, not the original write. A fourth,
> not-yet-located defect (or a fluke `nameof()`-as-value path during a sync
> serialization step) is likely needed to fully close the loop.
>
> Pin: `Directory.Packages.props` lines 98–101 hold CoreSync at `0.1.127`.
> `0.1.128-local` is a metadata-only re-stamp of `0.1.127` (verified
> byte-identical decompiled bodies).

---

## Defect 1 — `GetValueFromRecord` does not handle `Nullable<T>` or `enum`

**Assemblies / locations**

- `CoreSync.Sqlite.dll` 0.1.127, `CoreSync.Sqlite.SqliteSyncProvider.GetValueFromRecord(SqliteDataReader, int, Type)`
  - Decompiled at `/tmp/coresync-sqlite-decomp/CoreSync.Sqlite.decompiled.cs:939–985`
- `CoreSync.PostgreSQL.dll` 0.1.127, same shape in `CoreSync.PostgreSQL.PostgreSqlSyncProvider.GetValueFromRecord`
  - Decompiled at `/tmp/coresync-pg-decomp/CoreSync.PostgreSQL.decompiled.cs:1064`

**Symptom**

Reading a sync row where the target CLR property is `DateTime?`, `bool?`,
`int?`, or any `enum` (e.g. `VerificationState`) returns the raw SQLite
value via `r.GetValue(ord)` without coercion. If the cell already contains
a string (because SQLite has dynamic typing and the column has INTEGER or
NUMERIC affinity but a TEXT value was previously stored), the string flows
straight through into the in-memory `SyncItemValue` and is then serialized
to the server as `String`.

**Code shape (paraphrased from decompile)**

```csharp
// type dispatch handles only these concrete types:
if (type == typeof(string)) return r.GetString(ord);
if (type == typeof(DateTime)) return r.GetDateTime(ord);
if (type == typeof(int)) return r.GetInt32(ord);
if (type == typeof(bool)) return r.GetBoolean(ord);
// ... byte, char, short, long, decimal, double, float ...
return r.GetValue(ord);   // ← Nullable<T> and enum land here
```

`Nullable.GetUnderlyingType(type)` is never consulted; `type.IsEnum` is
never consulted.

**Root-cause hypothesis**

The dispatch was written against a fixed list of primitive CLR types. The
moment a model uses `DateTime?` (as `VocabularyProgress.UserDeclaredAt`
does) or an `enum` backed by `int` (as `VerificationState` does), the code
relies on whatever the underlying ADO.NET reader returns — which for
SQLite is governed by *cell* type, not *column* type, and can be
`string`.

**Verification steps (planned, not yet executed)**

1. Build a minimal CoreSync.Sqlite consumer with a model:
   `class M { Guid Id; DateTime? When; MyEnum State; }`
2. Manually `INSERT` a row using a raw SQL string that puts text into
   the `When` and `State` columns (e.g. `'foo'`).
3. Trigger a sync read; capture the produced `SyncItem.Values`.
4. Assert that current code returns the string verbatim, and that adding
   a `Nullable.GetUnderlyingType` / `IsEnum` branch coerces correctly.

**Confidence**: **HIGH.** The dispatch code is short, complete, and
visible. The omission is unambiguous.

**Workaround status**

- Indirect: PR #185's `RepairTaintedVocabularyProgressAsync` scrubs the
  three known affected columns on Captain's local DB at startup, so this
  defect cannot manifest on the read path again for those columns.
- No general-case workaround in our codebase. Any future `DateTime?` /
  `enum` column in any CoreSync-managed table is theoretically vulnerable
  on the read side if a tainted cell ever lands in the local SQLite.

---

## Defect 2 — `ConvertValueForColumn` silently passes unparsed strings to PostgreSQL

**Assembly / location**

- `CoreSync.PostgreSQL.dll` 0.1.127,
  `CoreSync.PostgreSQL.PostgreSqlSyncProvider.ConvertValueForColumn(object, string)` (or similar — exact name in decompile)
- Decompiled at `/tmp/coresync-pg-decomp/CoreSync.PostgreSQL.decompiled.cs:1576–1591`

**Symptom**

When the inbound `SyncItemValue` is a `string` and the target Postgres
column is `timestamp with time zone` (or `timestamp`), the code calls
`DateTime.TryParse(s, out var dt)`. On **success** it uses `dt`. On
**failure** it returns the original string unchanged. Npgsql then rejects
the parameter at the wire level with:

```
42804: column "UserDeclaredAt" is of type timestamp with time zone but expression is of type text
```

This is exactly the upload exception Captain saw repeatedly on
`POST /api/sync-agent/changes-bulk-complete/{guid}` (stuck batch
`1c18d82b-d160-424d-9ecf-bec51d04ba1e`).

**Code shape (paraphrased)**

```csharp
if (value is string s && columnType.StartsWith("timestamp"))
{
    if (DateTime.TryParse(s, out var dt)) return dt;
    return value;   // ← silently returns the unparsed string
}
```

**Root-cause hypothesis**

Defensive coercion was added for the common case (string-encoded
timestamps from JSON serialization), but the failure branch was written
as "return as-is" rather than "throw a `SynchronizationException` with a
useful diagnostic." This converts a recoverable client-side validation
problem into an opaque DB-level 42804 that retries forever.

**Verification steps (planned)**

1. Construct a `SyncItem` whose `Values` contains
   `("UserDeclaredAt", SyncItemValueType.String, "UserDeclaredAt")`.
2. POST it to a CoreSync.PostgreSQL endpoint targeting a table with a
   `timestamptz` column of that name.
3. Confirm Npgsql 42804 — proves the string reached the parameter.
4. Patch the failure branch to `throw` and re-run; confirm a
   `SynchronizationException` arrives at the client with a column-
   identifying message before the wire call is made.

**Confidence**: **HIGH.** The exact 42804 error and the column name "as
value" content match the decompiled behavior precisely. Fall-through
return is visible in the IL.

**Workaround status**

- Indirect: PR #185 prevents the tainted strings from ever being uploaded
  by repairing them at startup. Pipeline cannot retrigger.
- No general-case workaround. Any future tainted text in any timestamp
  column would reproduce the stuck-batch behavior.

---

## Defect 3 — `SyncItemValue.DetectTypeOfObject` is a pure runtime-type switch with no schema check

**Assembly / location**

- `CoreSync.dll` 0.1.127, `CoreSync.SyncItemValue.DetectTypeOfObject(object)`
- Decompiled at `/tmp/coresync-decomp/CoreSync.decompiled.cs:780–843`

**Symptom**

Once defect 1 demotes a value to `string`, this method classifies it as
`SyncItemValueType.String` with no warning, no provenance check against
the source column's declared type, and no log. The serialized
`SyncItem.Values` payload sent to the server therefore looks completely
well-formed — the server cannot detect the corruption at parse time and
only fails much later at the Npgsql parameter binding step (defect 2).

**Code shape (paraphrased)**

```csharp
static SyncItemValueType DetectTypeOfObject(object o)
{
    if (o is null) return SyncItemValueType.Null;
    if (o is string) return SyncItemValueType.String;
    if (o is DateTime) return SyncItemValueType.DateTime;
    if (o is bool) return SyncItemValueType.Boolean;
    // ... etc, runtime-type only
}
```

There is no parameter for the *declared* column / property type, so the
method has no way to notice "I was asked to classify what should be a
DateTime but I got a string."

**Root-cause hypothesis**

The serialization layer trusts the read layer (defect 1) absolutely. The
type-discovery contract is "whatever you hand me, I label." This is a
reasonable design *if* the read layer is sound; combined with defect 1
it's silent data laundering.

**Verification steps (planned)**

1. Construct a `SyncItemValue` from a string handed in where the source
   property is `DateTime?` and the source schema column is `INTEGER` (in
   our case, the actual SQLite affinity for the `UserDeclaredAt` column
   needs to be checked — it accepts text regardless).
2. Serialize → confirm `SyncItemValueType.String` is emitted.
3. Add a schema-aware overload `DetectTypeOfObject(object, Type
   declaredType)`; confirm it can throw or warn when actual ≠ declared.

**Confidence**: **HIGH** that the body is purely runtime-type. **MEDIUM**
that this is the right place to fix; an alternative is to fail-fast in
`GetValueFromRecord` (defect 1) so that this method never sees a
laundered value in the first place. We'd want both belt and suspenders.

**Workaround status**

- None in our codebase. PR #185 is purely a downstream cleanup.
- Any fix must live in CoreSync.

---

## Cross-cutting note: missing originator

None of the three defects above explain the *original write* of
`"UserDeclaredAt"` (the literal column-name string) into the
`UserDeclaredAt` cell. Defects 1+2+3 explain how a tainted string is
read, propagated, and rejected — but something had to put it there
first.

Hypotheses still open (none yet ruled in):

- A `nameof(UserDeclaredAt)`-style bug somewhere in CoreSync's
  `INSERT … VALUES (@p0, @p1, …)` parameter binding path where parameter
  *names* were accidentally bound to *values*. (Not yet found in
  decompile.)
- A migration/up-version hook that wrote literal column names as
  defaults. (Not present in CoreSync 0.1.122 or 0.1.127.)
- A one-time interaction with a partial schema change on Captain's
  machine. (Treat as ghost event unless we see it twice.)

When we do file upstream, the issue body should lead with this
"originator unknown" framing and ask the maintainer whether they're
aware of any code path that would produce a column-name-as-value write.

---

## Repository pin

- `Directory.Packages.props:98-101` → CoreSync packages at `0.1.127`.
- `0.1.128-local` exists but is a metadata-only re-stamp; bodies are
  byte-identical to `0.1.127`.

## Captain-side workaround

- PR #185 (`fix(sync): self-repair tainted VocabularyProgress rows from
  CoreSync corruption event`) — adds `RepairTaintedVocabularyProgressAsync`
  in `src/SentenceStudio.Shared/Services/SyncService.cs:674` and calls it
  from line 522 inside the existing `#if IOS || ANDROID || MACCATALYST`
  block. Repair runs on all three mobile platforms uniformly.
