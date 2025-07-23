# Prompt Instructions & Coding Cheat Sheets

This file collects important prompt instructions and coding best practices from `.github/prompts/` for quick reference in Cursor. Use these guidelines to ensure code quality, consistency, and best practices across the project.

---

## Table of Contents

- [Async Programming](#async-programming)
- [C# Code Style](#c-code-style)
- [.NET MAUI Guidelines](#net-maui-guidelines)
- [Unit Testing with xUnit](#unit-testing-with-xunit)
- [MAUI Layouts](#maui-layouts)
- [MAUI Memory Leaks](#maui-memory-leaks)
- [Upgrading to .NET MAUI](#upgrading-to-net-maui)

---

## Async Programming

**Best Practices:**
- Always `await` your tasks.
- Prefer `Task`/`Task<T>` over `async void` (except for event handlers).
- Name async methods with the `Async` suffix.
- Pass `CancellationToken` parameters.
- Use `ConfigureAwait(false)` in library/server code.
- Avoid blocking on async code (no `.Wait()` or `.Result`).
- Avoid fire-and-forget unless handled safely.

**See**: `.github/prompts/dotnet/async.prompt.md` for full details.

---

## C# Code Style

**Highlights:**
- Use XML doc comments for public members, explaining purpose and intent.
- Prefer `record` types for data, seal classes by default.
- Use range indexers and collection initializers.
- Explicitly mark nullable fields and use nullability attributes.
- Use `Try` methods for safe operations.
- Prefer `await` and propagate `CancellationToken`.
- Use `nameof` for symbol references and logging.
- Prefer implicit usings and file-scoped namespaces.
- Pattern matching: use `is` for null/type checks.

**See**: `.github/prompts/dotnet/codestyle.prompt.md` for comprehensive style guide.

---

## .NET MAUI Guidelines

**General:**
- Prefer `Grid` for layout, use `VerticalStackLayout`/`HorizontalStackLayout` (not `StackLayout`).
- Use `CollectionView` or `BindableLayout` (not `ListView`/`TableView`).
- Use `Border` instead of `Frame`.
- Register custom handlers in `MauiProgram.cs`.
- Use dependency injection for services and image sources.
- Handle permissions with the `Permissions` API.
- Implement error handling and caching for image loading.

**See**: `.github/prompts/dotnet/maui/maui.prompt.md` for more.

---

## Unit Testing with xUnit

**Key Points:**
- Use xUnit as the default framework.
- Prefer `[Theory]` over multiple `[Fact]`s for parameterized tests.
- Use fixtures for shared state.
- Follow Arrange-Act-Assert pattern.
- Use `ITestOutputHelper` for logging.
- Clean up resources and ensure test isolation.
- Use xUnit's built-in assertions, avoid third-party assertion libraries.

**See**: `.github/prompts/dotnet/testing.xunit.prompt.md` for full guidelines and examples.

---

## MAUI Layouts

- Prefer a flat visual tree (use `Grid` over nested layouts).
- Do not nest scrollable controls unless scrolling in different directions.
- Override `OnSizeAllocated` for layout changes, not event handlers.

**See**: `.github/prompts/dotnet/maui/maui-layouts.prompt.md` for more.

---

## MAUI Memory Leaks

- Avoid circular references on iOS/MacCatalyst (especially with `NSObject`).
- Use static event handlers or proxies to break reference cycles.
- Follow .NET MAUI handler patterns for event management.

**See**: `.github/prompts/dotnet/maui/maui-memory-leaks.prompt.md` for detailed patterns and examples.

---

## Upgrading to .NET MAUI

- Convert Xamarin projects to SDK-style and update target frameworks.
- Replace Xamarin.Forms/Essentials with .NET MAUI equivalents.
- Refactor layouts and update usages of `Frame`, `ScrollView`, etc.
- Add explicit `RowDefinitions`/`ColumnDefinitions` to `Grid`.
- Review and update implicit/explicit sizing and spacing.

**See**: `.github/prompts/dotnet/maui/maui-upgrade.prompt.md` for migration checklist.

---

For more details, see the original files in `.github/prompts/`. 