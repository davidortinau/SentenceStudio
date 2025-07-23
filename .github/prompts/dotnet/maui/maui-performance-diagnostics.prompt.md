# Copilot Prompt Instructions: Diagnose Runtime Performance in .NET MAUI

## Goal
Help me identify and resolve runtime performance issues in a .NET MAUI application using CLI-based tools during development.

## Context
This .NET MAUI app targets mobile and desktop platforms. I want to diagnose performance bottlenecks (e.g., slow screens, unresponsive UI, high CPU usage) using platform-agnostic, code-instrumentation and .NET CLI tools—not Visual Studio or IDE-specific profilers. CLI tools are preferred for integration with automation workflows or GitHub Copilot agents.

## Expectations
- Format: Provide CLI command snippets and concise explanations.
- Focus: Diagnose first. Recommend code improvements only *after* identifying slow paths.
- Tools: Use .NET CLI tools like `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`, and `Stopwatch` timing.
- Scope: Work only in development builds. Avoid suggestions that rely on production telemetry, native profilers, or IDE GUIs.
- Examples: Include full examples of code instrumentation or trace collection and interpretation.

## Prompt Examples

### Example 1 – Instrument code with timers
```
Instrument my `LoadDataAsync()` method using `System.Diagnostics.Stopwatch` to log how long it takes. Show how to log this to console for performance diagnostics.
```

### Example 2 – Monitor runtime counters
```
Use `dotnet-counters` to monitor CPU usage, GC heap size, and exceptions for my running .NET MAUI app. Show how to find the process ID and interpret the output.
```

### Example 3 – Trace performance hot paths
```
Guide me through using `dotnet-trace` to collect a performance trace from my .NET MAUI app, export it to speedscope format, and identify which method is slowing down a page load.
```

### Example 4 – Analyze trace file
```
I collected a `profile_trace.nettrace` file using `dotnet-trace`. Help me analyze it via CLI to list the top 10 slowest methods by inclusive time.
```

### Example 5 – Use GC dump to investigate memory issues
```
Use `dotnet-gcdump` to capture and analyze memory usage in my MAUI app. Show how to find large object allocations or memory leaks.
```

---

## Additional Tips for Prompting
- Use full sentences: e.g., "Show me how to time this method..."
- Be specific: name the method, page, or scenario you’re debugging.
- Give examples: e.g., “Navigating to SettingsPage is slow.”
- State expectations: “Use CLI, don’t suggest Visual Studio.”

## Review and Verify
Always confirm that Copilot’s commands work on your target platform and are safe to run in your development environment.