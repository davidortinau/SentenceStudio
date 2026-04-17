# Getting started with HelpKit

This sample demonstrates how to integrate Plugin.Maui.HelpKit into a .NET MAUI
app. Tap the Help entry (in the flyout, toolbar, or main page depending on the
sample) to open the in-app help pane.

## What you can ask

HelpKit grounds answers in the markdown files that ship with the app. Try
questions like:

- "How do I open help?"
- "What providers can I plug in?"
- "Why is the help pane not appearing?"

## Stub providers

By default the samples run with a stub `IChatClient` and a stub
`IEmbeddingGenerator`. These return canned text and deterministic fake vectors
so the app runs without cloud credentials. Answers are illustrative only —
replace the stubs with real providers before shipping.

## Next steps

1. Register your real `IChatClient` and `IEmbeddingGenerator` as keyed
   singletons under the HelpKit service key (`"helpkit"` by default).
2. Point `HelpKitOptions.ContentDirectories` at your own markdown folder.
3. Run ingestion once on startup; HelpKit caches the index and only re-ingests
   when a file changes or the pipeline fingerprint shifts.
