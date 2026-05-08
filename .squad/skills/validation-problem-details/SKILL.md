# Skill: ASP.NET Core Validation via `TypedResults.ValidationProblem`

**Owner:** Kaylee (Full-stack Dev)
**Captured from:** Wash's review of commit 398a7690 (Profile + Speech endpoints).

## When to use

Any minimal-API endpoint that accepts a request body or query parameters that
need server-side validation. This is the canonical "RFC 7807 problem details"
shape — every modern .NET client (System.Net.Http.Json, Refit, retrofit_dart,
Dio + json_serializable) understands it natively, so we don't need a
hand-rolled error contract.

## The pattern

### 1. Validation method shape

Return a `Dictionary<string, string[]>` keyed by the JSON property name (in
PascalCase to match the model property — `TypedResults.ValidationProblem`
preserves these keys verbatim into the response body):

```csharp
private static Dictionary<string, string[]>? ValidateUpdateRequest(
    UpdateProfileRequest request)
{
    var errors = new Dictionary<string, List<string>>();

    void Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
        {
            list = new List<string>();
            errors[field] = list;
        }
        list.Add(message);
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName))
        Add(nameof(request.DisplayName), "Display name is required.");
    else if (request.DisplayName.Trim().Length > MaxNameLength)
        Add(nameof(request.DisplayName),
            $"Display name must be {MaxNameLength} characters or fewer.");

    if (!string.IsNullOrWhiteSpace(request.Email)
        && !new EmailAddressAttribute().IsValid(request.Email.Trim()))
        Add(nameof(request.Email), "Email is not a valid address.");

    return errors.Count == 0
        ? null
        : errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
}
```

### 2. Endpoint usage

```csharp
var validationErrors = ValidateUpdateRequest(request);
if (validationErrors is not null)
    return TypedResults.ValidationProblem(validationErrors);
```

`TypedResults.ValidationProblem` returns HTTP 400 with a JSON body shaped like:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "DisplayName": ["Display name is required."],
    "Email": ["Email is not a valid address."]
  }
}
```

## Client side — Flutter / Dio

```dart
class ProfileValidationException implements Exception {
  ProfileValidationException(this.fieldErrors);
  final Map<String, List<String>> fieldErrors;
}

ProfileValidationException? _tryParseValidationProblem(DioException error) {
  final response = error.response;
  if (response == null || response.statusCode != 400) return null;
  final data = response.data;
  if (data is! Map<String, dynamic>) return null;
  final errors = data['errors'];
  if (errors is! Map<String, dynamic> || errors.isEmpty) return null;
  final fieldErrors = <String, List<String>>{};
  errors.forEach((field, messages) {
    if (messages is List) {
      fieldErrors[field] = messages.map((m) => m.toString()).toList();
    }
  });
  if (fieldErrors.isEmpty) return null;
  return ProfileValidationException(fieldErrors);
}
```

The repository converts `DioException` (status 400) to the typed exception and
**rethrows** rather than silently falling back to local cache — the user must
see what they typed wrong.

## Anti-patterns

- ❌ `return BadRequest("Display name is required")` — string body, no field
  attribution, clients can't render per-field errors.
- ❌ `return Problem(detail: "...")` for validation — that's RFC 7807 too but
  uses `ProblemDetails` (no `errors` map). Use `ValidationProblem` for
  field-level errors.
- ❌ Custom DTO `{ "errorMessage": "...", "fields": [...] }` — re-invents the
  wheel and breaks every framework's automatic 400 handling.
- ❌ Catching the 400 in the client and falling back to a cached value — the
  user thinks the save worked. **Always surface validation errors.**

## Checklist

- [ ] Validation method returns `Dictionary<string, string[]>?` (null = valid).
- [ ] Field names match `nameof(request.X)` — PascalCase property names.
- [ ] Endpoint short-circuits with `TypedResults.ValidationProblem` BEFORE any
      DB or domain work.
- [ ] Validation runs after trimming/normalising input (so trailing spaces
      don't blow up the "required" check).
- [ ] Client surfaces errors to the user — never silently caches the bad
      value.
- [ ] OpenAPI metadata declares `ValidationProblem<HttpValidationProblemDetails>`
      so generated clients know about the 400 shape.

## See also

- `src/SentenceStudio.Api/ProfileEndpoints.cs` — `ValidateUpdateRequest`,
  `UpdateProfile`.
- `lib/features/profile/data/profile_repository.dart` —
  `ProfileValidationException`, `_tryParseValidationProblem`.
- `.squad/skills/api-endpoint-review-checklist/SKILL.md` — Wash's broader
  endpoint review checklist this skill plugs into.
