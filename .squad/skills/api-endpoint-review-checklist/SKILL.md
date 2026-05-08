---
name: "api-endpoint-review-checklist"
description: "10-point checklist for reviewing new API endpoints in multi-user, dual-provider (PostgreSQL/SQLite) contexts"
domain: "api-design, security, validation"
confidence: "low"
source: "observed"
---

## Context

When reviewing new API endpoints in this project, check for:
- Multi-user data isolation (IDOR vulnerabilities)
- Input validation + error handling
- Performance anti-patterns (fetch-all-then-filter)
- Dual-provider concerns (PostgreSQL API, SQLite mobile sync)
- Consistency with existing endpoint patterns

This checklist emerged from reviewing commit 398a7690 (Profile, Speech, Maintenance endpoints).

## Patterns

### 1. **IDOR / Ownership Check**

Every endpoint that accepts a resource ID (profile, resource, import, etc.) MUST verify the authenticated user owns that resource BEFORE operating on it.

**Pattern:**
```csharp
var userProfileId = user.FindFirstValue("user_profile_id");
if (string.IsNullOrEmpty(userProfileId))
    return Results.Unauthorized();

// Check ownership BEFORE fetching resource
if (!string.Equals(userProfileId, profileId, StringComparison.Ordinal))
    return Results.Forbid();

var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId);
```

**Anti-pattern:**
```csharp
// ❌ WRONG — fetches resource first, THEN checks ownership
var resource = await repository.GetByIdAsync(resourceId);
if (resource.UserProfileId != userProfileId)
    return Results.Forbid();
```

**Why order matters:** The ownership check is cheap (string comparison). If you fetch the resource first, you've already done expensive DB work for a request you're going to reject.

---

### 2. **Fetch-All Anti-Pattern (CRITICAL)**

**NEVER** use `repository.ListAsync().FirstOrDefault(predicate)` in API endpoints.

**Why:** Fetches ALL rows from the table into memory, then filters client-side. Performance bomb at scale.

**Detection:**
```bash
grep -r "\.ListAsync().*\.FirstOrDefault" src/SentenceStudio.Api
```

**Fix:** Use direct DB query scoped by ID:
```csharp
// ❌ WRONG
var profile = (await repository.ListAsync()).FirstOrDefault(p => p.Id == profileId);

// ✅ CORRECT
var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == profileId);
```

---

### 3. **Input Validation**

All request bodies MUST be validated. Use `TypedResults.ValidationProblem` for structured error responses.

**Pattern:**
```csharp
if (string.IsNullOrWhiteSpace(request.DisplayName))
    return TypedResults.ValidationProblem(new Dictionary<string, string[]> {
        { nameof(request.DisplayName), new[] { "Display name is required" } }
    });

if (request.PreferredSessionMinutes < 1 || request.PreferredSessionMinutes > 480)
    return TypedResults.ValidationProblem(new Dictionary<string, string[]> {
        { nameof(request.PreferredSessionMinutes), new[] { "Session minutes must be between 1 and 480" } }
    });
```

**Anti-pattern:**
```csharp
// ❌ WRONG — commit message claims validation, code has none
// Accepts empty strings, negative numbers, malformed emails
```

**Existing precedent:** `FeedbackEndpoints.cs:50-54` validates length + emptiness (uses `BadRequest` strings, not ideal, but better than nothing).

---

### 4. **CancellationToken Propagation**

All async endpoint handlers MUST accept `CancellationToken cancellationToken` and pass it to repository/service calls.

**Pattern:**
```csharp
private static async Task<IResult> GetProfile(
    string profileId,
    ClaimsPrincipal user,
    [FromServices] UserProfileRepository repository,
    CancellationToken cancellationToken)  // ← Required
{
    var profile = await repository.GetByIdAsync(profileId, cancellationToken);
    return Results.Ok(MapToDto(profile));
}
```

**Why:** Without cancellation support, if the client drops the connection, the server keeps running the query. Wastes resources.

**Existing precedent:** `FeedbackEndpoints.cs:42`, `ChannelEndpoints.cs`, `ImportEndpoints.cs` all use `CancellationToken`.

---

### 5. **Logging**

All endpoints SHOULD log at key decision points (success, failure, ownership rejection).

**Pattern:**
```csharp
private readonly ILogger<ProfileEndpoints> _logger;

// Constructor injection
public ProfileEndpoints(ILogger<ProfileEndpoints> logger) => _logger = logger;

// Usage
_logger.LogInformation("User {UserId} updated profile {ProfileId}", userProfileId, profileId);
_logger.LogWarning("Profile {ProfileId} not found for user {UserId}", profileId, userProfileId);
```

**Existing precedent:** `FeedbackEndpoints.cs:44` injects `ILoggerFactory`.

---

### 6. **Error Handling**

Use structured `TypedResults.Problem` instead of plain `Results.Problem` strings.

**Pattern:**
```csharp
if (saved < 0)
{
    _logger.LogError("Profile save failed for user {UserId}", profileId);
    return TypedResults.Problem(
        title: "Save failed",
        detail: "Unable to save profile changes. Please try again.",
        statusCode: 500
    );
}
```

**Why:** Provides consistent error shape for clients (title, detail, status, type).

---

### 7. **Authorization Scope**

Endpoints that operate on ALL users (e.g., maintenance/migration tasks) MUST have explicit admin-only authorization OR per-user filtering.

**Anti-pattern:**
```csharp
// ❌ WRONG — MaintenanceEndpoints.cs:24-34
var userProfileId = user.FindFirstValue("user_profile_id");  // Extracted but NOT used
var migrated = await progressService.MigrateToStreakBasedScoringAsync();  // Operates on ALL users!
```

**Fix (per-user):**
```csharp
var migrated = await progressService.MigrateToStreakBasedScoringAsync(userProfileId);
```

**Fix (admin-only):**
```csharp
group.MapPost("/migrate-streak", MigrateStreak)
    .RequireAuthorization("AdminOnly");  // Add policy
```

---

### 8. **Response Shape Stability**

For endpoints supporting mobile clients (Flutter, MAUI), response shapes MUST be stable across versions.

**Pattern:**
- Use explicit DTOs (not `dynamic` or `object`)
- Document nullability (`string?` vs `string`)
- Comment on forward-compatibility fields:
  ```csharp
  // ElevenLabsApiKey: Accepted in PUT but not yet persisted (always null in GET).
  // Will be added to UserProfile schema in future migration.
  ElevenLabsApiKey: null
  ```

---

### 9. **Dual-Provider Concerns**

All data modifications (POST/PUT/DELETE) MUST work correctly in BOTH PostgreSQL (API) and SQLite (mobile sync).

**Check:**
- Does the endpoint use raw SQL? (If so, check quoting: PostgreSQL requires `"PascalCase"`, SQLite is case-insensitive)
- Does it depend on PostgreSQL-specific features (JSONB, arrays, CTEs)?
- Will the same operation work when synced to mobile via CoreSync?

**Existing pattern:** See `.squad/skills/ef-dual-provider-migrations/SKILL.md`.

---

### 10. **Route Consistency**

Endpoints SHOULD follow existing route conventions:

- **GET collection:** `/api/v1/{resource}` (e.g., `/api/channels`)
- **GET single:** `/api/v1/{resource}/{id}` (e.g., `/api/v1/profile/{profileId}`)
- **POST (create):** `/api/v1/{resource}`
- **PUT (update):** `/api/v1/{resource}/{id}`
- **DELETE:** `/api/v1/{resource}/{id}`

**Anti-pattern:**
```csharp
// MaintenanceEndpoints.cs — scoped to /api/v1/vocabulary/progress
// But it's a debug-only admin operation, not a vocabulary API surface
var group = app.MapGroup("/api/v1/vocabulary/progress").RequireAuthorization();
```

**Better:** `/api/v1/maintenance` or `/api/v1/admin/maintenance`.

---

## Examples

**Good endpoint (ChannelEndpoints.cs:22-32):**
```csharp
private static async Task<IResult> GetChannels(
    ClaimsPrincipal user,
    [FromServices] ChannelMonitorService channelService)
{
    var userProfileId = user.FindFirstValue("user_profile_id");
    if (string.IsNullOrEmpty(userProfileId))
        return Results.Unauthorized();

    var channels = await channelService.GetAllAsync(userProfileId);  // ← Scoped by userId
    return Results.Ok(channels);
}
```

**Bad endpoint (ProfileEndpoints.cs:50-61):**
```csharp
private static async Task<IResult> GetProfile(
    string profileId,
    ClaimsPrincipal user,
    [FromServices] UserProfileRepository repository)
{
    var ownership = ResolveOwnership(profileId, user);
    if (ownership is not null) return ownership;

    var profile = (await repository.ListAsync()).FirstOrDefault(p => p.Id == profileId);  // ← Fetches ALL profiles!
    if (profile is null) return Results.NotFound();

    return Results.Ok(MapToDto(profile));
}
```

---

## Anti-Patterns

1. **Fetch-all-then-filter** — `repository.ListAsync().FirstOrDefault(predicate)`
2. **No ownership check** — operating on resource without verifying user owns it
3. **Missing validation** — accepting request bodies without validation
4. **No CancellationToken** — async methods without cancellation support
5. **No logging** — silent success/failure makes debugging impossible
6. **Broken multi-tenant boundary** — extracting userId but not using it to filter operations
7. **Misleading commit messages** — claiming "ValidationProblemDetails on bad input" when no validation exists
8. **Returning stale data** — `SaveAsync` returns int, endpoint returns unsaved entity

---

## Detection Commands

```bash
# Find fetch-all anti-pattern
grep -r "\.ListAsync().*\.FirstOrDefault" src/SentenceStudio.Api

# Find endpoints without CancellationToken
grep -l "private static async Task<IResult>" src/SentenceStudio.Api/*.cs | \
  xargs grep -L "CancellationToken"

# Find endpoints without logging
grep -l "private static async Task<IResult>" src/SentenceStudio.Api/*.cs | \
  xargs grep -L "ILogger"
```

---

## Related Skills

- `.squad/skills/ef-dual-provider-migrations/SKILL.md` — PostgreSQL/SQLite migration patterns
- `.squad/decisions/inbox/wash-fetch-all-antipattern.md` — Decision record for this pattern

---

## Confidence: Low

This is the first observation of these patterns. Confidence will increase after:
- Applying the checklist to 3+ endpoint reviews
- Finding false positives/negatives in the detection heuristics
- Team feedback on the review rubric
