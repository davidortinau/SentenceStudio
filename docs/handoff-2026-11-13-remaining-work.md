# Remaining work audit (2026-11-13)

Written for the Captain after the night's Library work on `library-split-view`. Sources: the
branch and remotes, `docs/native-port-backports.md`, `docs/native-review-backlog.md`,
`docs/native-port-departures.md`, `docs/skill-flexibility-plan.md` and `.squad/decisions/inbox`.

## Where things stand

- **Branch:** `library-split-view` is pushed (02235970 plus this note), 17 commits ahead of
  `native-platform-heads` and `main` (they are at the same commit). Not merged. Per the branch
  routine, it should merge into `native-platform-heads` once you're happy with it on Duotopia.
- **SwiftUI bridge:** branch `split-view` is pushed with previews .53-.60; `main` is behind it.
  SentenceStudio pins preview.60 from the local feed (`~/work/Microsoft.SwiftUI/artifacts`).
- **Duotopia:** has 02235970 (Release, production API). It installed while the phone was locked, so
  it hasn't been opened yet.

## Check first on Duotopia (needs your hands)

1. **Long-press labels on toolbar icons.** Every Library, Study, Activity Log and Skill editor
   toolbar icon now has a title (departure 83). The iPad simulator shows no label on a long press
   (the press fires the button), so whether iOS 27 shows one on a phone toolbar is unproven. If it
   doesn't, the titles still serve VoiceOver and the large-text viewer, and I'll look for the
   native mechanism (for example a bridge `.help` or a `Label` style change).
2. **Skills:** archive, restore, delete and search, with the editor open beside the list, on your 20
   skills; the count under the title; and the counts on every Library row.
3. **Switching apps** no longer restarts SentenceStudio (the 0x8BADF00D fix from 8f8487ed). You
   said "looks good" after that install; worth one more check after a long session.

## Decisions waiting on you

1. **Skill flexibility proposal** (`docs/skill-flexibility-plan.md`): a skill states its language,
   a Focus (grammar or situation, needs a schema change), and practising a skill without choosing
   vocabulary (source 3 needs the Learning Value Gate). Nothing is built.
2. **Merge `library-split-view`** into `native-platform-heads` (and later `main` and a deploy).
   Nothing in this branch changes the schema or the API.
3. **Korean wording** of the new strings (draft): Skills archive/restore/show
   (`Skills_*`), `Skills_ProfileCount` "스킬 프로필 {0}개", `Skills_ArchivedCount` "보관됨 {0}개",
   `Skills_WaitForChange`, `Skills_SaveFirst`, `Skills_DeleteConfirm`, `Vocabulary_WordCount`,
   `Vocabulary_ChooserMore`.

## Library plan: what's left on iOS

From your list (native lists with icons; swipe Delete, Study this, Archive for skills):

- **Done:** Resources, Vocabulary and Skills as native lists with swipe actions, counts and search;
  the Library split view; counts on every Library row.
- **Minimal Pairs:** still the older page. Make its list a native List with swipe Delete (asks
  first) and Study this (start the pair's practice), a count under the title, and search, as the
  other three have.
- **Forms, pickers, banners, empty states, confirmations:** the remaining "native way" pass. Forms
  still draw custom cards (the Skill editor's fields card, the word editor), banners are custom
  alerts (`style.Alert`), and confirmations mix the system confirmation dialog with the custom
  `ResourceDeleteConfirmation` (the Skill editor's Delete still uses the custom one). Each needs a
  look at what SwiftUI offers (Form/Section, `.alert`, `ContentUnavailableView`) before changing.
- **Remaining icon-only buttons** in pages (not toolbars): Sam panel header, quiz and activity
  close/previous/next, Reading, Scene gallery (backlog).

## Parity (B82, other clients)

The web/MAUI pages and Android lack what iOS gained in the Library:

- **Resources and Vocabulary:** list-row Delete (confirmed) and Study this; Android also lacks the
  count subtitles and the 100-row window isn't needed there (Compose lazy list).
- **Skills:** archive view, Archive/Restore, list Delete, search and counts. Web/MAUI has no archive
  view at all, so a skill Sam archives can't be restored there. Android's Skill editor still has
  More with Delete and no Archive.
- **Library counts** on every row (Android's Library list).

## Known failures and test debt

- **iOS contract tests:** 2 long-standing failures (`SourceButtons_KeepNativeHit...`,
  `SamPresentationUsesOnlyTheSwiftUIFacade`).
- **SwiftUIViews tests (macOS app, run by hand):** 4 Vocabulary cases fail on the committed code
  before tonight's change (wide hidden filter column, phone filter sheet, typing in place, layout
  remount). Probably from the Vocabulary chooser/filter-menu work (8f8487ed and earlier), whose
  view tests weren't rerun because this suite isn't part of `dotnet test`. Fix these next.
- **Install script:** its 6-second launch check fires early on a cold first launch (seen three
  times); lengthen it.

## Backlog worth scheduling (from `docs/native-review-backlog.md`)

- The confirmation-dialog dismiss-before-action workaround is copied three times; fix once in the
  bridge.
- Library counts after a background sync update on the next return to Library (Resources too); a
  sync-applied event would let them follow at once.
- The dashboard "Total words" doesn't refresh after a Library delete.
- The dead iOS phone filter sheet for Vocabulary lists every value; remove or convert it.
- Text-matching contract tests (several tests assert source text; they break on harmless rewrites).

## Open WebApp/MAUI defects (from `docs/native-port-backports.md`)

B41 (quiz resume), B52 (theme contrast), B58 (onboarding toast in English), B67/B68 (Find
Duplicates manager and merge result), B71 (Import commit button hidden on phones), B73/B74
(Import localization and AI failure reporting), B82 (Library list parity, above).
