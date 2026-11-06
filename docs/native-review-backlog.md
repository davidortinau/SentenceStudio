# Native review backlog

Review findings recorded instead of fixed during a review loop (correctness items are fixed;
these wait for a later pass). One line each: where, then the point.

## Import Content (iOS, review of 4e1921a9..29d1fe54, 2026-11-06)

- `src/SentenceStudio.Native/Features/Import/ImportContentCoordinator.cs:736`: after a commit the target-resource list is read right away (one more redraw and a database read the result page does not show); it could be read lazily when Import more returns to the wizard.
- `src/SentenceStudio.Native.iOS/Features/Import/ImportContentView.cs:62`: `Page` detaches the previous draw's rows grid as a side effect of drawing; that move belongs to the scene, which owns the drawn nodes.
- `src/SentenceStudio.Native/Features/Import/ImportServiceText.cs:50`: the service's English is mapped back to keys with literals and regexes, which break silently when the service text changes; the service should return codes and arguments (B74).
- `src/SentenceStudio.Native.iOS/App/ApplicationScene.Import.cs:53`: `_importDrawn.Clear()` before the first draw is redundant (`ImportContentView.Page` clears it).
