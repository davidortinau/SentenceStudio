# Save buttons across the native screens (#217) and the toolbar More menu (#218)

2026-11-15. Read from the iOS and Android sources at `main` `46f6ec14` plus this branch, and
checked running on the "iPhone Duo issue218" simulator (iOS 27.1, closed pose, landscape).

## The rule

Issue #217 (2026-11-09) asked for the toolbar Save to go from the word form. The Captain's
decision the next day replaced that (departure 66 in `docs/native-port-departures.md`): every
editor has one Save, a plain text toolbar action at every width; none in the More menu, no Save
or Cancel under the form; Back leaves without saving.

## Editors: one plain text Save in the toolbar

| Screen | iOS | Android |
|---|---|---|
| Vocabulary word | follows the rule (`VocabularyWordEditView.cs`) | follows the rule (`AndroidSliceShell.VocabularyDuplicates.Compose.cs`) |
| Learning Resource (add and edit) | follows the rule (`LearningResourceView.cs`); seen running on Add Resource | follows the rule (`AndroidSliceShell.ResourceForm.Compose.cs`); seen running on the #219 check |
| Skill Profile (add and edit) | follows the rule (`SkillProfileFormView.cs`) | follows the rule (`AndroidSliceShell.Skills.Compose.cs`) |
| Profile | follows the rule (departure 87) | **differs:** "Save Profile" is a button in the page (`AndroidSliceShell.Profile.Compose.cs:109`) |
| YouTube channel | follows the rule ("Save Channel", 2026-11-13) | **differs:** "Save Channel" is a button in the page (`AndroidSliceShell.YouTubeImport.Compose.cs:618`) |

The two Android differences are not covered by a recorded decision (departures 87 and the
2026-11-13 form pattern are iOS). They need the Captain's call: move them to the app bar as on
iOS, or leave them.

## Saves that are not an editor's Save (left as they are)

- Vocabulary word, "Save or undo your changes": Save and Discard under the message, both
  clients (Captain 2026-11-14).
- Settings: "Save Preferences" row under the auto-advance slider (departure 88; Android the same).
- YouTube Import: "Save as Resource" after a transcript is fetched (creates a resource).
- Diary: the entry's Save in the activity's own header row (the source layout).
- Reading: "Remember this word" in the word panel.
- Sam: Save on a suggested memory, and Save/Cancel while editing one.
- Word editor examples: Save/Cancel while editing one example sentence.

No screen has a second editor Save in a More menu or under its form.

## #218: the More button on a toolbar out of room

The report (Learning Resources, folded Duo, landscape) no longer reproduces: More lists List
view, Filters, Add Resource and Search, and Add Resource opens its form. The cause was toolbar
items with no title (an icon with only an accessibility label), which give the system's More
menu nothing to list; departure 83 titled the browse pages' icons. This branch titles the rest:
Conversation (scenario and language menus), Describe a Scene (gallery), Vocabulary Matching
(More) and Sam's header (conversations, new, resize, close). In the same pose each still shows
its icon and acts when tapped; none of the four overflows there, so their More listing was not
seen running. `NativeToolbarOverflowContractTests` holds the titles.
