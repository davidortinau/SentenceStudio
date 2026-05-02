# Blazor Hybrid: JS-interop init must follow state, not firstRender

## When this applies

A Blazor Hybrid (or server) page does JS interop in `OnAfterRenderAsync(firstRender:true)` to initialize DOM-bound widgets (Tom Select, Chart.js, monaco, leaflet, etc.) — and that init is gated on component state (`if (someMode) await InitWidgetsAsync()`).

If `OnInitializedAsync` ever **defers** that state's resolution (waits on async events, sync completion, post-login routing, etc.), `firstRender` will fire BEFORE the state is correct, and your init silently no-ops. Symptom: widgets are empty on first visit but render correctly after the user navigates away and back (which re-mounts the component with state already resolved).

## Detection cues

- "It works the second time" / "works after I nav back"
- Empty Tom Select / Chart / map controls only on cold start
- `OnInitializedAsync` contains an early `return` before the main load
- `OnAfterRenderAsync` reads component state inside `if (firstRender)`

## Fix patterns

### Pattern A — Re-invoke from the deferred completion handler (minimal)

```csharp
private void OnInitialSyncCompleted()
{
    SyncService.InitialSyncCompleted -= OnInitialSyncCompleted;
    _ = InvokeAsync(async () =>
    {
        await LoadStateAsync();      // resolves the state firstRender saw stale
        StateHasChanged();           // renders widget host elements

        if (NeedsWidgets && jsModule is not null)
        {
            await Task.Delay(50);    // let DOM settle
            await InitWidgetsAsync();
        }
    });
}
```

### Pattern B — Idempotent flag (more robust, scales to multiple deferral paths)

```csharp
private bool widgetsInitialized;

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender)
        jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/app.js");

    if (!widgetsInitialized && NeedsWidgets && jsModule is not null)
    {
        widgetsInitialized = true;
        await InitWidgetsAsync();
    }
}
```

Pattern B fires on every render until it succeeds, so any post-deferral `StateHasChanged` triggers it. Slightly more chatter, but no risk of forgetting a deferral path.

## Anti-patterns

- ❌ Putting JS init solely inside `if (firstRender)` when state can be deferred.
- ❌ Calling JS interop without checking `jsModule is not null` (it's only loaded in firstRender).
- ❌ Calling init twice without a destroy/dispose first (Tom Select, charts, etc. throw on re-init).

## Reference

- Fix applied to `src/SentenceStudio.UI/Pages/Index.razor` (Dashboard) — 2026-05-02. See `.squad/agents/troubleshooter/history.md`.
