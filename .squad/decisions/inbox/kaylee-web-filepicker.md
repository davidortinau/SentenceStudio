# Decision: WebFilePickerService uses JS interop with scoped DI

**Date:** 2025-07-18
**Author:** Kaylee (Full-stack Dev)
**Status:** Implemented

## Context

`WebFilePickerService` threw `NotSupportedException`. The `IFilePickerService` interface needed a working implementation for any code that calls it programmatically in the WebApp.

## Decision

- Implemented via JS interop (`filePickerInterop.pickFile`) that creates a hidden `<input type="file">`, reads the selected file as a byte array, and returns it to C#.
- Changed DI registration from `AddSingleton` to `AddScoped` because `IJSRuntime` is circuit-scoped in Blazor Server — a singleton cannot hold a scoped dependency.
- Follows the existing `window.*` global object pattern used by `audioInterop.js`.

## Files Changed

- `src/SentenceStudio.WebApp/wwwroot/js/filePicker.js` — new JS interop module
- `src/SentenceStudio.WebApp/Platform/WebFilePickerService.cs` — injects IJSRuntime, calls JS
- `src/SentenceStudio.WebApp/Program.cs` — `AddSingleton` → `AddScoped`
- `src/SentenceStudio.WebApp/Components/App.razor` — added `<script>` tag

## Impact

Any service or page that injects `IFilePickerService` will now get a working implementation on web. Existing Blazor pages using `InputFile` directly are unaffected.
