# Skill: Blazor Nav State Preservation

> Preserve page state across Blazor navigations using an in-memory store keyed by a URL query parameter.

## When to Use

- A Blazor page shows results of an expensive operation (import, AI generation, search)
- User navigates away (e.g., to a detail page) and needs to return to the same results via browser Back
- Blazor re-initializes the component on each navigation, losing in-memory state

## Pattern

### 1. Create a Store Service

```csharp
public interface IResultStore<T>
{
    Guid Save(T result);
    T? TryGet(Guid key);
}
```

- Use `ConcurrentDictionary<Guid, (T Result, DateTime ExpiresAt)>` internally
- TTL of 30 minutes with lazy eviction on Save/TryGet
- Register as **Singleton** for single-user apps, **Scoped** for multi-user

### 2. After Operation Completes

```csharp
var key = Store.Save(result);
NavManager.NavigateTo($"/my-page?completed={key}", forceLoad: false);
```

### 3. On Page Init

```csharp
[SupplyParameterFromQuery(Name = "completed")]
public string? CompletedKey { get; set; }

protected override async Task OnInitializedAsync()
{
    if (Guid.TryParse(CompletedKey, out var key))
    {
        var cached = Store.TryGet(key);
        if (cached != null)
        {
            result = cached;
            // Skip to results view
        }
    }
}
```

### 4. On Reset/New Operation

Clear the URL: `NavManager.NavigateTo("/my-page", forceLoad: false);`

## Why This Works

- Browser Back preserves the URL query string, so the GUID key survives navigation
- No JS interop needed (unlike SessionStorage)
- Works identically in MAUI Hybrid and Blazor Server
- GUID is opaque — no data leakage in URL

## Gotchas

- Server restart clears the store. Acceptable for transient result data.
- Don't use for data that must survive app restarts — use a database or local storage instead.
- If the app becomes multi-user, switch to Scoped lifetime + per-user key prefixing.
