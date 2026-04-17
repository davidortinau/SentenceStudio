# Smoke: Mac Catalyst (net11.0-maccatalyst)

**Target sample:** `samples/HelpKitSample.MauiReactor` (primary; matches SentenceStudio shape)
**Estimated time:** 10-15 minutes
**Prereqs:** net11 preview SDK, MAUI workload, Xcode Command Line Tools

Run in order. Mark each line `- [x]` on pass or add a failure note.

- [ ] 1. `cd /Users/davidortinau/work/SentenceStudio/lib/Plugin.Maui.HelpKit && dotnet restore` returns exit 0.
- [ ] 2. `dotnet build samples/HelpKitSample.MauiReactor/HelpKitSample.MauiReactor.csproj -f net11.0-maccatalyst` returns exit 0, no warnings above level 3.
- [ ] 3. `dotnet build samples/HelpKitSample.MauiReactor/HelpKitSample.MauiReactor.csproj -t:Run -f net11.0-maccatalyst` launches the sample window.
- [ ] 4. Open Activity Monitor; the sample process is in Running state (not Not Responding).
- [ ] 5. Tap the Help button (or flyout Help item). Chat page opens as a modal.
- [ ] 6. Type "What is vocabulary?" and Send. Response streams token-by-token (visible typing effect).
- [ ] 7. Citation chip appears under the assistant bubble referencing a `test-corpus` `.md` file. Tap the chip — app does not crash.
- [ ] 8. Send 2 more questions ("How do I manage vocab?" and "Describe Word Association"). Both stream; both cite valid paths.
- [ ] 9. Close the chat modal. Reopen. Transcript is preserved (history persisted).
- [ ] 10. Force-quit the sample app (Cmd+Q). Relaunch. Reopen help. Transcript restored from SQLite.
- [ ] 11. Inspect SQLite:
    ```bash
    DB=~/Library/Containers/com.davidortinau.helpkitsample.mauireactor/Data/Library/Application\ Support/HelpKitSample.MauiReactor/helpkit/helpkit.db
    sqlite3 "$DB" "SELECT Role, substr(Content,1,60) FROM message ORDER BY CreatedAtUtc DESC LIMIT 10;"
    sqlite3 "$DB" "SELECT Fingerprint, IngestedAtUtc FROM ingestion_fingerprint;"
    sqlite3 "$DB" "SELECT count(*) FROM answer_cache WHERE ExpiresAt > strftime('%s','now');"
    ```
    Expected: >= 6 messages (3 user + 3 assistant). One `ingestion_fingerprint` row. >= 1 live cache entry.
- [ ] 12. `ls -la ~/Library/Containers/...<sandbox>.../helpkit/vectors.json` exists and is non-empty.
- [ ] 13. Ask the same first question ("What is vocabulary?") a second time. Response arrives perceptibly faster (cache hit).
- [ ] 14. Open Console.app, filter by the sample process. Confirm:
    - No `Fatal exception`, no `NullReferenceException`, no stack traces.
    - At least one `helpkit.ingest.chunks` metric line.
    - Zero lines containing `"STRICTLY from the provided documentation"` or `"You are the in-app help assistant"`.
- [ ] 15. Tap Clear. Transcript empties. Re-run step 11; `message` row count drops to 0 for the current user.

**Sign-off:** Tester initials + UTC date on pass. File a GitHub issue on any failure with the line numbers that failed and a log excerpt.
