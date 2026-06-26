# Share-to-Vocabulary (iOS) — design & status

Ingest content into SentenceStudio from outside the app: select text (or share a web page)
in any app → Share → SentenceStudio → the text is saved as vocabulary, auto-classified
Word / Phrase / Sentence / Idiom, grouped under a "Shared Inbox" learning resource. Stretch
goal: the same via Siri ("Add to SentenceStudio").

This is iOS-first. It is also a deliberate **dogfooding** exercise: iOS app extensions in
.NET MAUI are an under-documented path, and capturing/root-causing that friction (then filing
upstream) is a primary deliverable.

## Architecture (deferred capture → App Group queue → app drains)

```
[Any app] --Share--> [SentenceStudio Share Extension  (tiny native iOS process)]
     captures NSItemProvider (public.plain-text | public.url)
     writes a SharedIngestItem JSON into the App Group container:
       group.com.simplyprofound.sentencestudio/share-inbox/{id}.json
     shows quick confirmation + CompleteRequest()      (NO auth, NO AI, NO DB)
                         |
                         v  (next app foreground / launch)
[Main app] SharedIngestProcessor.DrainAsync()
     gate on authenticated active_profile_id (else defer, leave queued)
     for each queued item:
        - Url  : WebArticleFetcher → readable text
        - Text : use payload as-is
        - ContentImportService.ParseContentAsync (ContentType.Auto → classify)
        - ContentImportService.CommitImportAsync → append to ONE "Shared Inbox"
          LearningResource (dedup save + ResourceVocabularyMapping)
        - remove processed item from the queue
     surface result; existing SyncService syncs as normal
```

**Why deferred:** an iOS Share Extension is a separate, memory-limited process that cannot host
the MAUI/Blazor app, the auth session, or the AI service. So it only *captures*; the
authenticated main app does the classify-and-save using the **existing** import pipeline.

## What is built (and unit-tested)

| Phase | Component | Location | Tests |
|------|-----------|----------|-------|
| 1 | Shared cross-process queue contract — `ISharedIngestQueue`, `SharedIngestItem`, `SharedIngestKind`, `SharingConstants`, atomic file-backed impl | `src/SentenceStudio.Sharing/` (netstandard2.0) | 9 |
| 3-core | `SharedIngestProcessor` / `ISharedIngestProcessor.DrainAsync` — auth-gated, single-flight, per-item failure isolation, appends to one "Shared Inbox" resource via `IContentImportService` | `src/SentenceStudio.Shared/Services/SharedIngestProcessor.cs` | 7+ |
| 4 | `IWebArticleFetcher` + `WebArticleFetcher` + pure `HtmlReadability` reducer (regex strip + entity decode, 8 000-char cap, thin-page title/meta fallback) | `src/SentenceStudio.Shared/Services/WebArticleFetcher.cs` | 24 |

Full unit suite green after this work (721/721 at time of writing). No new package
dependency was added for HTML parsing (regex-based reducer; revisit AngleSharp/HtmlAgilityPack
only if accuracy proves insufficient).

Reuses (not rebuilt): `IContentImportService.ParseContentAsync` / `CommitImportAsync`,
`VocabularyWord.LexicalUnitType`, `FreeTextVocabularyExtractionResponse`, multi-tenant repo
scoping.

## Remaining work (gated on Apple Developer Portal — owner: David)

### Phase 0 — App Group + provisioning (Apple Developer Portal, team NYHGX6KCDG)
1. Identifiers → **App Groups** → register `group.com.simplyprofound.sentencestudio`.
2. App IDs → `com.simplyprofound.sentencestudio` → Capabilities → enable **App Groups** →
   assign that group → Save.
3. App IDs → register **new** `com.simplyprofound.sentencestudio.ShareExtension` (Explicit) →
   enable **App Groups** → assign the same group.
4. Profiles → regenerate the main app's development + distribution profiles (now carrying the
   App Groups entitlement); create development + distribution profiles for the extension App ID.
5. Refresh locally (Xcode → Settings → Accounts → Download Manual Profiles).

Repo-side wiring done **after** the portal (so the working DX24 build isn't broken early):
create `src/SentenceStudio.iOS/Platforms/iOS/Entitlements.plist` with
`com.apple.security.application-groups` = the group, wire `CodesignEntitlements` in the iOS
csproj; the extension target gets its own Entitlements.plist with the same group.

### Phase 2 — iOS Share Extension target (the .NET MAUI dogfooding surface)
- Separate app-extension target; principal class extends `SLComposeServiceViewController` (or a
  minimal custom `UIViewController`).
- `NSExtension` Info.plist: `NSExtensionPointIdentifier = com.apple.share-services`;
  `NSExtensionActivationRule` with `NSExtensionActivationSupportsText` and
  `NSExtensionActivationSupportsWebURLWithMaxCount = 1`.
- Read `ExtensionContext.InputItems` → `NSItemProvider` (`public.plain-text` / `public.url`),
  enqueue a `SharedIngestItem` via a `FileSystemSharedIngestQueue` pointed at the App Group
  container resolved with `NSFileManager.DefaultManager.GetContainerUrl(...)`, then
  `CompleteRequest`. Keep dependency-light (no MAUI/EF/AI).
- Bundle the extension inside the `.app`; confirm it appears in the share sheet.
- Capture every csproj/MSBuild/codesign/packaging gap for upstream filing.

### Phase 3-wiring (with Phase 2)
- iOS `ISharedIngestQueue` registration (App Group container path resolver) in the iOS head DI.
- Drain hook on app foreground (App lifecycle Resumed / startup, after auth is known); surface a
  toast/local notification ("Added N items from sharing").
- Verify the user's target/native language source (the processor currently reads preferences
  `target_language` / `native_language`, defaulting Korean/English — confirm these are the real
  keys before device testing).

### Phase 5 — End-to-end validation on DX24 (mandatory gate)
Build + install to DX24; from Safari select text → Share → SentenceStudio, and share a URL;
reopen the app; verify saved/classified/grouped/deduped/synced + not-signed-in deferral; no
cross-tenant leakage. Install over the app (never uninstall — DX24 is production).

## Phase 6 (stretch, DESIGN ONLY) — "Add to SentenceStudio" via Siri

Goal: speak "Add to SentenceStudio …" (or run a Shortcut) and have spoken/parameter text land in
the same ingest queue, so the app drains it identically to a Share.

### Recommended approach: App Intents (iOS 16+)
- Define an **App Intent** (e.g. `AddToSentenceStudioIntent`) with a single text parameter
  (the phrase to capture) and a short title/description.
- Provide an **App Shortcuts** provider so the intent is offered to Siri and the Shortcuts app
  without manual user setup, with natural-language phrases ("Add to SentenceStudio").
- The intent's `perform` enqueues a `SharedIngestItem { Kind = Text, Payload = <spoken text> }`
  via the same `ISharedIngestQueue` (App Group container). No new ingest path — it reuses the
  Phase 1 queue + Phase 3 drainer. (If the intent runs in-process when the app is foreground, it
  could even trigger `DrainAsync` immediately.)
- Result UX: a brief dialog/snippet confirming "Added to SentenceStudio."

### Fallback for older OS: SiriKit Custom Intent
- A SiriKit **Intents extension** with a custom `INIntent` + intent definition; the handler
  enqueues to the same App Group queue. More boilerplate; only needed for pre-iOS-16 reach.

### .NET-iOS feasibility — VERIFY BEFORE IMPLEMENTING (do not assume)
App Intents and App Shortcuts are Apple-API-level well-established, but their **binding maturity
in .NET for iOS (Microsoft.iOS) is the open question** and must be confirmed before committing:
- Check current `Microsoft.iOS` bindings for the `AppIntents` framework
  (types like `AppIntent`, `AppShortcutsProvider`, parameter attributes) and whether they are
  surfaced for the targeted SDK.
- Watch for the common constraint that App Intents/App Shortcuts must be discovered from a
  Swift/ObjC metadata bundle — a small **native (Swift) App Intents extension** that calls into
  shared storage may be required if the managed bindings don't expose the discovery hooks.
- Track upstream: `dotnet/macios` issues/discussions on App Intents support; file a minimal repro
  if a gap blocks the managed path (on-mission dogfooding).
- SiriKit Custom Intents have older, more-proven .NET-iOS support and are the safer fallback if
  App Intents bindings aren't ready.

No implementation in this phase — this section is the design + the explicit verification gate.

## Out of scope (v1)
Image/OCR sharing; Android share intent (iOS-first); inline classification inside the extension;
any new AI classifier (the existing extraction pipeline is reused).
