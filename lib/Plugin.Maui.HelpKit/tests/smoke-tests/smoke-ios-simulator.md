# Smoke: iOS Simulator (net11.0-ios)

**Target sample:** `samples/HelpKitSample.Shell`
**Simulator:** iPhone 15 Pro, iOS latest
**Estimated time:** 15-20 minutes

- [ ] 1. `xcrun simctl list devices available | grep 'iPhone 15 Pro'` shows a booted-capable device. Boot it: `xcrun simctl boot "iPhone 15 Pro"` then `open -a Simulator`.
- [ ] 2. `dotnet build samples/HelpKitSample.Shell/HelpKitSample.Shell.csproj -f net11.0-ios -p:RuntimeIdentifier=iossimulator-arm64` returns exit 0.
- [ ] 3. `dotnet build samples/HelpKitSample.Shell/HelpKitSample.Shell.csproj -t:Run -f net11.0-ios` installs + launches on the simulator.
- [ ] 4. Simulator shows the shell flyout with "Help" item visible (flyout-icon helper configured).
- [ ] 5. Tap flyout > Help. Modal chat page appears with "Ask me anything about this app." empty-state.
- [ ] 6. Type "How do I add a vocabulary word?" and Send. Response streams; keyboard retracts; bubble does not jump under the notch.
- [ ] 7. Rotate simulator to landscape (Cmd+Right Arrow). Chat reflows; bubbles still visible; safe-area respected on both sides.
- [ ] 8. Send 2 more questions. Confirm citations render as chips below each assistant bubble.
- [ ] 9. Force-quit the app in the simulator's app switcher (swipe up). Relaunch via `xcrun simctl launch booted <bundle-id>`. Reopen help. Transcript restored.
- [ ] 10. Pull the app container: `xcrun simctl get_app_container booted <bundle-id> data`. Record the printed path as `$APP_DATA`.
- [ ] 11. Inspect SQLite:
    ```bash
    sqlite3 "$APP_DATA/Library/Application Support/helpkit/helpkit.db" \
      "SELECT Role, substr(Content,1,60) FROM message ORDER BY CreatedAtUtc DESC LIMIT 10;"
    sqlite3 "$APP_DATA/Library/Application Support/helpkit/helpkit.db" \
      "SELECT Fingerprint FROM ingestion_fingerprint;"
    ls -la "$APP_DATA/Library/Application Support/helpkit/vectors.json"
    ```
    Expected: >= 6 message rows; 1 fingerprint row; vectors.json non-empty.
- [ ] 12. Enable VoiceOver on simulator: Features > Toggle Software Keyboard off; Settings > Accessibility > VoiceOver > On. Reopen help. Swipe right through elements. Confirm:
    - "Help, heading level 1" announced.
    - Each message reads role + excerpt.
    - Send and Clear buttons announce their localized accessibility names.
- [ ] 13. Toggle `options.Language = "ko"` in the sample's `MauiProgram.cs`, rebuild, rerun. Confirm 도움말, 닫기, 대화 지우기, 보내기, 질문을 입력하세요 render.
- [ ] 14. `xcrun simctl spawn booted log stream --predicate 'process == "HelpKitSample.Shell"' --level debug` for 30s while asking one more question. Confirm no unhandled exceptions and no system-prompt fingerprint phrase leakage.
- [ ] 15. Tap Clear. Transcript empties immediately. Re-run SQLite query in step 11 — `message` row count drops to 0 for current user.

**Sign-off:** initials + UTC date.
