# Native activity port: departures from the original app

Places where the native iOS and Android activities behave differently from the
original Blazor/MAUI app (`src/SentenceStudio.UI`). Most are bug fixes. The
recommendation is to keep all of them.

Captain: mark each row **Keep** or **Revert**. Rows left blank are kept.

Related: `docs/native-port-backports.md` lists the same bug fixes as work items
for the WebApp and MAUI app.

## Errors no longer save fake results

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 1 | Translation, Describe a Scene and Word Association: when AI grading fails, show an error and save nothing | Saved a 0% score or a wrong answer | Keep |
| 2 | Cloze: when the AI fails, show "could not generate" | Showed "No cloze sentences" | Keep |
| 3 | Conversation: errors are shown, and no empty reply bubbles appear | Failures were silent; an empty AI reply added an empty bubble | Keep |

## Data fixes

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 4 | Minimal Pairs uses the signed-in learner | Used user "1" for everyone, so a real learner's pairs never showed | Keep |
| 5 | Reading does not save the dictionary lookup word | Saved it reversed (English and Korean swapped) and with no owner | Keep |
| 6 | Diary checks the owner when updating an entry and saving feedback | No owner check | Keep |
| 7 | How Do You Say replays saved audio | Re-generated audio every time and added a duplicate history row | Keep |

## Behavior changes

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 8 | Number Drill shows a message for non-Korean target languages | Silently ran the Korean drill | Keep |
| 9 | Number Drill counts "Listen & Place" answers and shows feedback | Ignored them (0/0 summary) | Keep |
| 10 | Number Drill shows Disambiguate hints | A nesting bug hid them | Keep |
| 11 | Number Drill: Next during the 1.5-second pause no longer skips an item | Skipped an item | Keep |
| 12 | Number Drill summary fills in median time and "Patterns to Review" from your answers | Always blank | Keep |
| 13 | Cloze drops sentences where the word can't be blanked out | Showed them with the answer visible | Keep |
| 14 | Reading keeps the "audio generation failed" message visible | Hid it instantly | Keep |
| 15 | Conversation sends its opening instructions as system instructions | Combined prompt that Azure's content filter rejects (chat opened empty) | Keep |
| 16 | Shadowing applies Slow speed in the audio player | Slow was ignored (speech service has no speed) | Keep |
| 17 | Describe a Scene accepts https image links only | Accepted any link | Keep |
| 18 | Translation and Writing block moving to another sentence while a grade is pending | A late grade could land on the wrong sentence | Keep |

## Presentation

| # | Departure | Original behavior | Decision |
|---|---|---|---|
| 19 | Pop-up messages in several activities are inline banners | Toasts/pop-ups | Split by kind: errors banner, success/info toast (implemented 81c2cede) |
| 20 | Android Translation puts the answer box above the buttons | Answer box and buttons in one row | Keep |
| 21 | iOS Minimal Pairs delete dialog is titled "Delete A vs B?" | "Confirm Delete" | Keep |
| 22 | Video Watching transcript scrolls with the page | Fixed 300-pt scrolling box | Keep |
| 23 | Describe a Scene shows accuracy color as a badge | Colored left border | Keep |
| 24 | Hard-coded English strings have Korean translations | English only | Keep |

## Kept exactly like the original (also your call)

| # | Behavior | Possible change | Decision |
|---|---|---|---|
| 25 | Writing clears text you typed while the previous sentence was being graded | Keep the typed text | Keep the clearing |
| 26 | Number Drill's "I was correct" fixes the session score but not long-term mastery | Also correct mastery | Yes, also correct mastery. Implemented (`c3836877`): the stored attempt becomes correct and SM-2 is recomputed as a correct grade; WebApp backport B37 |
