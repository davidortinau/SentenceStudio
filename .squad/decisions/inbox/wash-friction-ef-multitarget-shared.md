### 2026-06-17: EF Core migrations blocked on multi-targeted Shared project

**By:** Wash (Backend Dev)
**Context:** Per-user timezone branch (`squad/per-user-timezone-plan-dates`)
**Type:** Tooling friction (dogfooding)

---

#### Problem

`dotnet ef migrations add` fails with `MSB4057: The target "ResolvePackageAssets" does not exist in the project` even when `--framework net10.0` is specified.

**Exact invocation that fails:**

```bash
dotnet ef migrations add AddUserProfileIanaTimeZoneId \
  --framework net10.0 \
  --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
  --startup-project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
```

**Environment (verified):**
- Selected SDK: 11.0.100-preview.5.26302.115 (via root global.json pin)
- dotnet-ef tool: 10.0.0
- Shared project TFMs: `net10.0;net11.0-ios;net11.0-android;net11.0-maccatalyst;net11.0-macos`

**Root cause:** The Shared csproj uses conditional `<Compile Remove>` blocks that partition migrations by platform (server vs mobile). The EF tooling's `--framework` flag selects the TFM for build, but the tool still cannot resolve `ResolvePackageAssets` because the multi-target MSBuild evaluation path diverges before the framework selection takes effect. This is a known limitation when mixing EF tooling with MAUI-style multi-targeted projects.

---

#### Workaround (current)

Hand-write dual-provider migrations following established patterns:
- PostgreSQL migration in `src/SentenceStudio.Shared/Migrations/` (type: `text`)
- SQLite migration in `src/SentenceStudio.Shared/Migrations/Sqlite/` (type: `TEXT`)
- Match timestamp across both providers
- Update both `ApplicationDbContextModelSnapshot.cs` files manually

See `.squad/skills/ef-dual-provider-migrations/SKILL.md` for the full procedure.

---

#### Mandatory validation step

After any migration (hand-written or otherwise), run:

```bash
bash scripts/validate-mobile-migrations.sh
```

This builds Mac Catalyst, launches via maui devflow, and scans logs for SQLite errors. Never skip this step -- it catches type mapping mismatches and missing columns.

---

#### Stale documentation (follow-up needed)

The following docs claim Shared targets plain `net10.0` -- this is STALE as of the net11 MAUI head migration:

1. **AGENTS.md line ~132:** "The Shared project targets plain net10.0" -- incorrect. Shared is multi-targeted.
2. **copilot-instructions.md (Database Migrations section):** "The Shared project targets plain net10.0 and works fine with EF tooling. There is no TFM conflict" -- incorrect. There IS a TFM conflict.

**Action:** A separate documentation-only PR should correct these statements. Do NOT edit AGENTS.md on a feature branch.

---

#### Upstream issue status

Searched `dotnet/efcore` for: "ResolvePackageAssets", "MSB4057", "multi-target MAUI migrations", "TargetFrameworks MAUI". No exact match found. This appears to be a known-but-unfiled intersection of EF tooling limitations with MAUI multi-targeting. Web search confirms the general guidance is "move EF to a single-targeted project" -- which is architecturally undesirable here because the same DbContext serves both server and mobile.

**Recommendation:** File upstream issue against dotnet/efcore requesting `--framework` to properly isolate TFM evaluation in multi-targeted projects with conditional compilation. Repro is minimal: any project with `<TargetFrameworks>` including platform TFMs + `<Compile Remove>` conditions + EF design-time factory.

---

#### Related skills

- `.squad/skills/ef-dual-provider-migrations/SKILL.md` -- full hand-write procedure (already documents this limitation)
- `.squad/skills/dotnet-sdk-detection/SKILL.md` -- SDK/TFM diagnostic procedure
