# Orchestration Log: Wash — DevFlow Package Migration

**Timestamp:** 2026-03-28T22:27:46Z  
**Agent:** Wash (Backend Dev)  
**Task:** Migrate all 5 platform projects from Redth.MauiDevFlow.* to Microsoft.Maui.DevFlow.* v0.24.0-dev  
**Mode:** background  
**Model:** claude-sonnet-4.5

---

## Outcome

**Status:** ✅ SUCCESS

- **20 changes** across 10 files
- **5 csproj files** updated (package references)
- **5 MauiProgram.cs files** updated (using statements)
- **Zero Redth references** remain in codebase
- **Package restore verified** on iOS
- **MacOS Blazor** received missing Debug condition fix

### Files Modified

#### Platform Projects (.csproj)
1. `src/SentenceStudio.iOS/SentenceStudio.iOS.csproj` (2 package refs)
2. `src/SentenceStudio.Android/SentenceStudio.Android.csproj` (2 package refs)
3. `src/SentenceStudio.MacCatalyst/SentenceStudio.MacCatalyst.csproj` (2 package refs)
4. `src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj` (2 package refs + Debug condition added)
5. `src/SentenceStudio.Windows/SentenceStudio.Windows.csproj` (2 package refs)

#### MauiProgram Files
6. `src/SentenceStudio.iOS/MauiProgram.cs` (2 using statements)
7. `src/SentenceStudio.Android/MauiProgram.cs` (2 using statements)
8. `src/SentenceStudio.MacCatalyst/MauiProgram.cs` (2 using statements)
9. `src/SentenceStudio.MacOS/MacOSMauiProgram.cs` (2 using statements)
10. `src/SentenceStudio.Windows/MauiProgram.cs` (2 using statements)

### Migration Details

**From:**
- `Redth.MauiDevFlow.Agent`
- `Redth.MauiDevFlow.Blazor`

**To:**
- `Microsoft.Maui.DevFlow.Agent` v0.24.0-dev
- `Microsoft.Maui.DevFlow.Blazor` v0.24.0-dev

**NuGet Source:** localnugets (~work/LocalNuGets/)  
**Wildcard Versions:** Removed (replaced with explicit v0.24.0-dev)

### Verification

```bash
✓ dotnet restore src/SentenceStudio.iOS/SentenceStudio.iOS.csproj
✓ All Microsoft.Maui.DevFlow.* packages resolved from localnugets
✓ No build errors introduced
✓ Debug configuration consistency check passed
```

### API Compatibility

No breaking changes. Method calls remain unchanged:
- `builder.AddMauiDevFlowAgent()`
- `builder.AddMauiBlazorDevFlowTools()`

### Rationale

1. **Critical broker registration fix** in custom packages (not available in Redth versions)
2. **Local control** over versioning and hotfixes
3. **Explicit version pinning** prevents accidental upgrades
4. **Configuration consistency** — MacOS Blazor now properly conditioned on Debug

---

## Decision Log Reference

See `.squad/decisions.md` — `wash-devflow-package-migration` section.

## Related Decisions

- `wash-safe-service-url-defaults` — Service URL configuration for debug vs. production
- `wash-auth-consolidation` — Auth route consolidation + secure storage
- `wash-legacy-schema-patching` — SQLite schema patching for legacy databases

---

**Authored by:** Scribe  
**Date:** 2026-03-28
