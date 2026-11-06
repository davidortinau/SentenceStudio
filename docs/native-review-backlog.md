# Native review backlog

Review findings recorded instead of fixed during a review loop (correctness items are fixed;
these wait for a later pass). One line each: where, then the point.

## Import Content (iOS, review of 4e1921a9..29d1fe54, 2026-11-06)

- `src/SentenceStudio.Native/Features/Import/ImportContentCoordinator.cs:736`: after a commit the target-resource list is read right away (one more redraw and a database read the result page does not show); it could be read lazily when Import more returns to the wizard.
- `src/SentenceStudio.Native.iOS/Features/Import/ImportContentView.cs:62`: `Page` detaches the previous draw's rows grid as a side effect of drawing; that move belongs to the scene, which owns the drawn nodes.
- `src/SentenceStudio.Native/Features/Import/ImportServiceText.cs:50`: the service's English is mapped back to keys with literals and regexes, which break silently when the service text changes; the service should return codes and arguments (B74).
- `src/SentenceStudio.Native.iOS/App/ApplicationScene.Import.cs:53`: `_importDrawn.Clear()` before the first draw is redundant (`ImportContentView.Page` clears it).

## Vocabulary Quiz autoplay and launch (review of `48c97189..4e1921a9`, 2026-11-06)

- **9. Off-main-thread work and Android parity of Back while loading.** `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs:612` (StartQuizAsync), `src/SentenceStudio.Native.Android/AndroidSliceShell.cs:356`. Moving database work off the main thread at one call site was the wrong altitude. That Task.Run is reverted: iOS has a UIKitSynchronizationContext, and the presenter is not thread-safe. If quiz database work must leave the UI thread, do it in the presenter or data layer for every command (Open, Retry, Resume, Start fresh, Next). Android also has no queued Back while the quiz loads; decide whether it should match iOS.
- **10. Autoplay request workflow copied in both heads.** `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.cs:74` and `src/SentenceStudio.Native.iOS/App/ApplicationController.Quiz.cs:292`. Prompt-shown dedupe, one request per turn, the linked token and its compare-and-clear are repeated in both heads (AGENTS.md: no platform copies of workflows). A presenter-owned `PromptShown(turnId)` that keeps the request's cancellation would remove both copies.
- **11. Android timing logs bypass ILogger.** `src/SentenceStudio.Native.Android/AndroidSliceShell.cs:360` and `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.cs:81` use `Android.Util.Log.Info` instead of the head's `ILogger` pipeline (AndroidServiceHost), so the "quiz opening" and "prompt on screen" lines skip the logging configuration the presenter's "autoplay started/finished" lines go through.
- **13. Effect keys from identity hashes are not unique.** `src/SentenceStudio.Native.Android/AndroidEffectKey.cs:32`. `RuntimeHelpers.GetHashCode` can repeat for two live instances, so "a new instance restarts the effect" is not guaranteed (a photo render report or viewer focus could be skipped). A per-instance sequence number or reference tracking would be exact.
- **14. Effect keys allocate on every quiz recomposition.** `src/SentenceStudio.Native.Android/AndroidSliceShell.Quiz.Compose.cs:34,50`. `AndroidEffectKey.Of` runs twice per recomposition (each keystroke in the answer field): a params array, a boxed bool, escaped strings and a join, even when the key is unchanged. Typed overloads or a cached key would avoid it.

## YouTube channels and import history (iOS, review of `548b9a61..527c6a50`, 2026-11-06)

- **API timestamps are host-local.** `src/SentenceStudio.Api/Program.cs:76` (also WebApp and Workers) sets `Npgsql.EnableLegacyTimestampBehavior`, so `timestamp with time zone` values are read back as host-local `DateTime` (Kind Local) and model JSON carries the host's offset (for example `MonitoredChannel.LastCheckedAt` from `/api/channels`). It predates this branch and stays out of scope; the import history now converts to UTC (`VideoImportResponse`) and the native client compares instants, but every other endpoint still returns host-local times.
