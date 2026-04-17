# HelpKit features

Plugin.Maui.HelpKit provides conversational in-app help grounded in your own
markdown documentation. It is designed to be honest about its limits: answers
are retrieved from your corpus and cited back to source files, and the library
refuses to speculate when retrieval falls below the configured similarity
threshold.

## Native MAUI UI

The help pane is a plain MAUI `ContentPage` (CollectionView + Entry). No
Blazor, no WebView. This keeps binary size small and renders on every MAUI
target (Android, iOS, Mac Catalyst, Windows).

## Bring-your-own AI

HelpKit does not ship, bundle, or recommend an AI model. You register an
`IChatClient` and an `IEmbeddingGenerator` in your app startup; HelpKit
resolves them through `Microsoft.Extensions.AI`. Supported providers today:

- OpenAI / Azure OpenAI
- Azure AI Foundry (used by the SentenceStudio production app)
- Ollama (local inference)
- Any other `Microsoft.Extensions.AI` implementation

## Three host patterns

The samples in this folder illustrate three hosting styles:

1. Shell — flyout-based navigation, `AddHelpKitShellFlyout` adds a Help entry.
2. Plain NavigationPage — no Shell; developer invokes `IHelpKit.ShowAsync()` from a toolbar button.
3. MauiReactor — MVU / fluent UI; HelpKit resolves a presenter that works with MauiReactor's navigation.

## Citations and refusal

Every answer the assistant produces is validated against the retrieved chunks.
Citations that point at non-existent chunks are stripped before the response
reaches your users. When retrieval falls below threshold the library returns a
polite refusal instead of calling the LLM at all.
