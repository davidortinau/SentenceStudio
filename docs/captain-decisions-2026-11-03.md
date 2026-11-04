# Decisions for the Captain (2026-11-03)

Eight decisions are waiting. Each section says what happens today, why it matters, the
options, what I recommend, and the short answer I need. Facts come from the code on
`native-platform-heads` (local, not pushed) unless a section says otherwise.

| # | Decision | Urgency | My recommendation |
|---|---|---|---|
| 1 | Sync requests without the native owner header (GitHub #204) and the installed MAUI apps | High: data exposure in production today | Close headerless sync in production with the next API deploy, after you confirm the MAUI apps can stop syncing |
| 2 | Deletes made on a device | High: a device can stop syncing | The server accepts deletes of the learner's own rows, and records them so other devices delete too |
| 3 | Phase 4 audio: server clip cache or per-device upload | Medium: cost only | A, the server clip cache |
| 4 | Lookup history (words looked up in Writing and Reading) | Low | Store lookups as activity rows and add a "Recent lookups" list; no new table |
| 5 | Number Drill: lenient grading of "Disambiguate" answers | Low, but it changes stored mastery | Grade each choice exactly, as the generator intends |
| 6 | Reading "Remember this word" can leave a word with no resource | Low | Use the native path that creates and links the word in one step |
| 7 | Four Dashboard questions from the native fidelity work | Low | Accept a and b; yes to c and d |
| 8 | Four Android Settings and Profile questions from the native fidelity work | Low to medium | Accept a for now; yes to b and c; yes to d as a separate piece of work |

---

## 1. Headerless sync (GitHub #204) and the installed MAUI apps

### What happens today
The API hosts CoreSync, the engine that syncs device databases with the server. Since
phase 1 of the sync work, the native iOS/Android apps send a header,
`X-SentenceStudio-Native-Owner`, naming the signed-in learner. With that header the
server checks every row: a device downloads only its learner's rows, and an upload that
touches another learner's row is refused (`src/SentenceStudio.Api/Sync/NativeOwnerSyncProvider.cs`).

A sync request **without** the header gets no owner checks at all
(`NativeOwnerSyncProvider.GetNativeOwner` returns null, and the request is passed straight
to CoreSync):

- a download returns **every learner's rows** in the synced tables;
- an upload can write or delete **any** row, including another learner's.

The request still needs a valid sign-in token, so the caller must have an account. But
registration is open (`AuthEndpoints.cs`; it needs a confirmed email), so anyone who
registers can call sync without the header and receive everyone's data.

Who syncs without the header today: the **installed MAUI apps**, the old app on DX24,
Pixel 5 and your Mac (`SentenceStudio.MauiCompatibility` and `AppLib` register CoreSync
without the header). The web app does not use CoreSync; it reads the server database
directly.

On `main`, which is what production runs unless you deployed this branch, the owner
checks do not exist at all: every sync request is unchecked.

### Why it matters now
- **Privacy:** this is the cross-tenant leak #204 describes, and it is open in production.
- **Phase 3 release:** after phase 3, a conversation's scenario link is text (for example
  `scn:first-meeting`). The installed MAUI apps read that column as a number. When the
  production API with phase 3 sends them a conversation that has a scenario, their
  conversation page cannot read it. No such conversation exists on the local server; I
  don't know about production.

### Options
| Option | What changes | Consequence |
|---|---|---|
| **A. Close headerless sync (recommended)** | A setting in the API (`Sync:AllowHeaderlessClients=false`) makes every sync route refuse a request without the owner header (403). Covered by a PostgreSQL test (a headerless download, upload, save-version and store-id are all refused; the native path still works). About half a day of work. | The installed MAUI apps stop syncing. They keep working with the data already on each device, and nothing on the device is deleted; new practice on them stays on that device. The leak is closed. Phase 3 can ship to production without breaking them, since they no longer download. |
| B. Teach the MAUI apps to send the header | Add the owner header and native-style owner handling to the MAUI sync client, then ship new MAUI builds to all three devices. | Their uploads would then be checked, and old MAUI databases hold rows the checks refuse (rows with no owner, deletes, legacy list tables), so their sync would likely stop anyway until those are cleaned. Several days of work on code that is being retired. |
| C. Leave it open until the native apps replace MAUI | Nothing now. | The leak stays open in production, and phase 3 cannot ship to production until the MAUI apps are gone. |

### What I need from you
1. Are you still using the MAUI apps on DX24, Pixel 5 or your Mac day to day, and does it
   matter if they stop syncing (they keep working offline)?
2. A, B or C. If A: should the switch go out with the next production API deploy, or now
   on `main` as a hotfix ahead of the native release?

---

## 2. Deletes made on a device

### What happens today
CoreSync records a delete as an item that carries only the row's ID. The server's owner
check refuses **every** delete in a device upload
(`NativeOwnerSyncProvider.ExistingRowsBelongToOwnerAsync`), and refusing one item refuses
the whole upload. The native apps delete synced rows in three places:

| Where | What gets deleted | Effect today |
|---|---|---|
| Learning Resources: remove a word from a resource | the word's link to the resource (`ResourceVocabularyMapping`) | If the link was already uploaded, **that device's sync stops** and stays stopped: the refused delete is offered again on every sync. (Links added and removed before they were uploaded are handled locally.) |
| Minimal Pairs: delete a pair | the pair and its answers | Before today the device's sync stopped the same way (since phase 2a). Today's change keeps such deletes on the device instead: sync continues, but the pair stays on the server, the web and other devices, and can come back to this device if it changes elsewhere. |
| Describe a Scene: delete images | the image rows | Same as Minimal Pairs (kept on the device since today). |

Deletes on the web happen directly in the server database, but no device ever receives
them: the server sends a device only rows it can prove belong to that learner, and a
deleted row has no data left to check.

### Options
| Option | What changes | Consequence |
|---|---|---|
| **A. Owner-checked deletes with a record of what was deleted (recommended)** | The server accepts a device delete when the stored row belongs to that learner (it can look the row up by ID before deleting it). Every delete, from a device or the web, is also written to a small new table of deleted IDs with their owner and table, so the server can send each delete to that learner's other devices. One new synced table (a schema change on both databases), server code, and tests. About two to three days. | Deleting works the way you'd expect on every client. Removing a word from a resource no longer stops sync. |
| B. Device deletes reach the server only | The server accepts owner-checked device deletes; nothing more. About one day. | The server and web see device deletes; other devices keep the deleted rows until they are edited. Sync never stops over a delete. |
| C. Keep deletes on the device for every table | Extend today's change to word links too. A few lines. | Sync never stops, but a delete never leaves the device that made it. |

### What I need from you
A, B or C. If you choose A, I would ship C for word links right away so no device can get
stuck while A is built.

---

## 3. Phase 4: audio clips

### What happens today
Audio is generated by ElevenLabs through the API (`/api/v1/speech/synthesize`): quiz
word audio, How Do You Say, Shadowing and Number Drill. Every call is paid. Since phase 2a
the audio history list syncs, but the audio files do not: a clip made on your phone is a
file on your phone. When another client plays that history entry, it generates the clip
again (one more paid call), and the review fix today makes it keep that copy so it pays
only once per device. The API has no cache, and production already has a blob container
named `media` (storage account `storagersn72awybem6s`, `docs/deploy-runbook.md`) that no
code uses yet.

### Options
| Option | What changes | Consequence |
|---|---|---|
| **A. Server clip cache (recommended)** | The speech endpoint keeps each clip it generates in `media`, named by a hash of the voice, model, voice settings and exact text, and returns the stored clip when the same request comes again. No schema change, no app change. About one day, plus giving the API access to the storage account in Azure. | Each distinct clip is paid for once, across every client and every learner: your phone, the web and a reinstall all reuse it. The clip depends only on the text and voice, so a learner who requests the same text gets the same bytes; nothing private is exposed. Storage grows slowly (clips are small); a cleanup rule can come later. |
| B. Devices upload their own clips | A new upload/download endpoint scoped to the learner, a column on the audio history row pointing at the server copy (schema change on both databases), apps that upload after generating and download before generating, and a rule for deleting clips. About four to five days. | The exact clip travels with the history entry, including audio that was not generated (if a recording feature is added). Storage per learner. |
| C. Do nothing | | Each client pays once per clip it has not made itself. |

### What I need from you
A, B or C. Either A or B needs the API's managed identity granted access to
`storagersn72awybem6s` in Azure; I would prepare the change and you would approve the
Azure step.

---

## 4. Lookup history

### What happens today
In Writing, the "translate" helper used to save every AI translation as a new vocabulary
word with no resource (backport B7). Those orphan words cluttered vocabulary and, on
native, stopped sync (the server refuses words it cannot place). The web and native apps
now **do not save** lookups as words. You said earlier that you expect a lookup history
instead.

What is kept today:
- Reading: each successful word lookup writes an activity row, `UserActivity` with
  `Input = "Dictionary lookup: <word>"` (owner-scoped, synced). The translation itself is
  not stored.
- Writing: nothing is kept.

### Options
| Option | What changes | Consequence |
|---|---|---|
| **A. Activity rows plus a "Recent lookups" list (recommended)** | Writing records lookups the same way Reading does, with the translation in the row's text (`"Lookup: 물 → water"`). A "Recent lookups" list (web and native) reads those rows, with "Add to vocabulary" on each. No schema change; the rows already sync. About one to two days. | You can see and reuse what you looked up, on every client. |
| B. A dedicated lookup table | A new synced table (owner, text, translation, direction, activity, resource, time); schema change on both databases; the same list. About three days. | Cleaner data and search, at the cost of a migration. |
| C. Save lookups as vocabulary in a per-learner "Lookups" resource | Each lookup becomes a real word in an owned resource. | They sync and enter quizzes and spaced review automatically, which may not be what you want for one-off lookups. |
| D. No history | | Current behavior. |

### What I need from you
A, B, C or D, and: what do you want to do with a past lookup (review it, add it to a
resource, quiz on it)?

---

## 5. Number Drill: "Disambiguate" grading

### What happens today
In Disambiguate, the learner picks two number words (for example the native and Sino
forms). The shared grader normalizes number words before comparing, so some wrong-system
choices are **stored as correct** while the page shows them as wrong; for example 삼째 날
counted as 셋째 날. This affects stored mastery and "patterns to review" on every client.
The question generator's own comment says each choice should be graded exactly.

### Options
- **A. Exact grading for Disambiguate (recommended).** Only the intended form counts. Stored
  results match what the learner sees. Past attempts are not changed.
- B. Keep lenient grading and make the page show it as correct.

### What I need from you
A or B.

---

## 6. Reading: "Remember this word"

### What happens today
On the web, "Remember this word" first saves the word, then links it to the resource. If
the link fails, an owned word with no resource is left, the same orphan problem as B7. The
native app uses one call that creates and links the word together
(`CreateAndAttachOwnedVocabularyAsync`), which also handles duplicates and smart
resources.

### Options
- **A. Use the native path on the web (recommended).** One step, no orphans. A word that
  already exists in your vocabulary is linked instead of duplicated, as native does.
- B. Keep the web's two steps and delete the word if the link fails.

### What I need from you
A or B.

---

## 7. Dashboard questions (native fidelity, W3)

The native Dashboard was corrected to match the original web Dashboard (findings AF-D01 to
D11 in `docs/native-source-fidelity-status.md`). Four points need you:

**a. A visible "Doing well with:" heading.** Today's Insight groups plan words into
"struggling", "newly discovered" and "doing well". The web shows the doing-well group with
only a green color and no heading. AGENTS.md says meaning must not rest on color alone, so
the native apps now show a visible heading "Doing well with:" (Korean "잘하고 있는 항목:")
with a check icon. Recommendation: accept.

**b. Who sees the "Get started" welcome.** The web shows the new-learner welcome (Quick
Start and Add a Resource) when the learner owns no resources and no words. The native app
had a stricter rule: a learner with any vocabulary progress never saw it, even with no
resources of their own. That can only happen for progress on words owned by nobody
(rows from before words had owners). Native now follows the web rule. Recommendation:
accept.

**c. Regenerate today's plan on the test account to check two changes.** A completed
Vocabulary Review card now reopens as re-practice on Android, and the insight tiers have new
headings and colors. Today's plan for the test account (Kaylee) has only a Translate card
and no insight, so neither change can be seen in the running apps; they are covered by
tests. Regenerating replaces today's plan cards for that account (the old cards are kept as
archived rows; no progress is lost). Recommendation: yes, regenerate Kaylee's plan.

**d. An iPad simulator for the iPad layout check (AF-D11).** The welcome heading must adapt
at iPad widths. Checking it needs a signed-in iPad simulator. "SS Agent ipad2" exists but
is not one you approved, and agents cannot sign in. Recommendation: let me use
"SS Agent ipad2" (install in place, back up first); if it is not signed in, you sign it in.

What I need from you: accept or change a and b; yes or no for c and d.

---

## 8. Android Settings and Profile questions (native fidelity, W2)

Android now has Settings (appearance, text size, every quiz setting including the
direction and the 1 to 10 second auto-advance) and the Profile, Skills, Vocabulary and
Activity Log screens, reached from its menu in the iOS order. The logic behind these screens
moved from the iOS project into the shared native library, so both platforms run the same
code.

**a. How Android applies text size.** The Compose library we use cannot set display density
directly, so Android applies the text-size setting (85 to 150%) by changing the screen's font
scale through an older Android call (`Resources.UpdateConfiguration`, marked deprecated). It
creates no Android view, so it is within the UI rules. It works: 150% scaled the whole app
and survived a restart. The proper fix is a density setting in the Compose library, which is
an upstream change that needs your permission. Recommendation: accept for now, and let me
log it as a library gap.

**b. Exact theme colors on Android.** iOS uses the exact colors of the web app's ten themes
(copied from its CSS). Android derives its colors for the nine non-default themes from the
shared theme list: readable, but not identical. Moving the exact color tables into shared
code would make all clients match. Recommendation: yes (about half a day).

**c. A reversible data check for Skills and Vocabulary.** The Skills list, edit, save and
delete flows, and saving a vocabulary word, were not tried on the device because the test
account has no skills and a save writes data. The check would create one throwaway skill and
delete it, and change one word and change it back, on Kaylee's test account. Recommendation:
yes.

**d. Saving the profile from the native apps.** On the web you can edit and save your
profile (name, languages, session length, level). On iOS and Android the profile screen
shows your data but Save is disabled, because devices only download the profile row from the
server; nothing sends a device's change back. Making Save work needs a small API endpoint
that saves the signed-in learner's own profile, and the apps calling it (about one day).
Recommendation: yes, as its own piece of work.

What I need from you: accept or change a; yes or no for b, c and d.

Follow-ups already queued (no decision needed): move the iOS Vocabulary screen onto the new
shared code, give Android's segmented buttons and tab markers the theme colors (they still use
the default purple), and add "Harvest example sentences" on both platforms.

---

## Fixes I will make next unless you object
These are bugs, not choices:
- Translation's sentence list turns an AI failure into "No sentences returned" (same class
  as B12).
- `ClozureService` and `TranslationService` keep the last request's words in a shared
  field on the web, where every signed-in learner shares the service. Nothing reads it
  today; I would remove the field.
- `VocabQuiz.razor` still has hard-coded English and one message that shows raw exception
  text.
