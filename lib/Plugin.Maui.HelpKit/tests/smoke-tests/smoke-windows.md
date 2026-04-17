# Smoke: Windows (net11.0-windows10.0.19041.0)

**Target sample:** `samples/HelpKitSample.Plain` (any host works; Plain is simplest)
**Machine:** Windows 11 x64, dev mode on, MSIX sideload permitted
**Estimated time:** 15-20 minutes

- [ ] 1. `dotnet build samples\HelpKitSample.Plain\HelpKitSample.Plain.csproj -f net11.0-windows10.0.19041.0` returns exit 0.
- [ ] 2. `dotnet build samples\HelpKitSample.Plain\HelpKitSample.Plain.csproj -t:Run -f net11.0-windows10.0.19041.0` launches the app window.
- [ ] 3. Tap Help button. Modal chat page opens with empty state.
- [ ] 4. Type "What is a skill profile?" and Send. Response streams token-by-token.
- [ ] 5. Citation chip renders below assistant bubble. Clicking it does not crash.
- [ ] 6. Ask 2 more questions. All three stream and cite.
- [ ] 7. Resize window (drag corner from 1200x800 to 600x900 and back). Chat bubbles reflow without overlap.
- [ ] 8. Close the app. Relaunch. Reopen help. Transcript restored from SQLite.
- [ ] 9. Inspect SQLite (adjust path to match packaged vs unpackaged):
    ```powershell
    # Unpackaged:
    $DB = "$env:LOCALAPPDATA\HelpKitSample.Plain\helpkit\helpkit.db"
    # Packaged MSIX (adjust PFN):
    # $DB = "$env:LOCALAPPDATA\Packages\<PackageFamilyName>\LocalState\helpkit\helpkit.db"
    sqlite3 $DB "SELECT Role, substr(Content,1,60) FROM message ORDER BY CreatedAtUtc DESC LIMIT 10;"
    sqlite3 $DB "SELECT Fingerprint FROM ingestion_fingerprint;"
    Get-Item ($DB | Split-Path | Join-Path -ChildPath vectors.json)
    ```
    Expected: >= 6 message rows; 1 fingerprint row; vectors.json > 0 bytes.
- [ ] 10. Windows Settings > Accessibility > Narrator > On. Reopen help. Tab through controls; Narrator announces Title, Send, Clear, Close with localized names.
- [ ] 11. Toggle Windows Dark mode (Settings > Personalization > Colors > Dark). Confirm chat page bubbles remain legible; no white-on-white or black-on-black.
- [ ] 12. Switch to Light mode. Same check.
- [ ] 13. Set `options.Language = "ko"` in sample, rebuild, rerun. Confirm 도움말, 닫기, 대화 지우기, 보내기 etc.
- [ ] 14. Visual Studio Output window during the run shows zero unhandled exceptions. Grep the output pane for `"STRICTLY"` and `"You are the in-app help assistant"` — must return zero matches.
- [ ] 15. In-app Clear. Transcript empties. Re-run sqlite3 query — message row count drops to 0 for current user.

**Sign-off:** initials + UTC date.
