# Smoke: Android Emulator (net11.0-android)

**Target sample:** `samples/HelpKitSample.Plain`
**Emulator:** Pixel 6, API 34, x86_64
**Estimated time:** 20-30 minutes

- [ ] 1. Start emulator: `~/Library/Android/sdk/emulator/emulator -avd Pixel_6_API_34 -no-snapshot-load &`. Wait for boot: `adb wait-for-device`.
- [ ] 2. `dotnet build samples/HelpKitSample.Plain/HelpKitSample.Plain.csproj -f net11.0-android -c Debug` returns exit 0.
- [ ] 3. Install: `dotnet build samples/HelpKitSample.Plain/HelpKitSample.Plain.csproj -t:Run -f net11.0-android`. Confirm `adb shell pm list packages | grep helpkit` shows the app.
- [ ] 4. Launch the app (should auto-launch). Record package name: `PKG=com.davidortinau.helpkitsample.plain` (adjust per csproj).
- [ ] 5. Tap the Help button. Modal opens with empty state.
- [ ] 6. Type "How do I track vocabulary streaks?" and Send. Response streams. Bubble renders with citation chip.
- [ ] 7. Press the back gesture (swipe from left edge). Modal dismisses cleanly; previous page restored.
- [ ] 8. Reopen help. Ask 2 more questions. Confirm all three user+assistant pairs visible.
- [ ] 9. Rotate emulator (Cmd+Left Arrow). Chat reflows. No height clipping on the nested citation CollectionView (flagged as untested).
- [ ] 10. Kill the app: `adb shell am force-stop $PKG`. Relaunch: `adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1`. Reopen help. Transcript restored from SQLite.
- [ ] 11. Inspect SQLite:
    ```bash
    adb shell "run-as $PKG sqlite3 files/helpkit/helpkit.db \"SELECT Role, substr(Content,1,60) FROM message ORDER BY CreatedAtUtc DESC LIMIT 10;\""
    adb shell "run-as $PKG sqlite3 files/helpkit/helpkit.db 'SELECT Fingerprint FROM ingestion_fingerprint;'"
    adb shell "run-as $PKG ls -la files/helpkit/"
    ```
    Expected: >= 6 message rows. 1 fingerprint row. `vectors.json` present and > 0 bytes.
- [ ] 12. Enable TalkBack: Settings > Accessibility > TalkBack > On. Reopen help. Swipe right through elements. Confirm:
    - "Help" heading announced with heading-level semantic.
    - Each bubble reads role + excerpt.
    - Clear / Close / Send buttons announce their localized accessibility names.
- [ ] 13. Switch device locale to Korean (Settings > System > Languages > Add 한국어 > drag to top). Relaunch app. Chrome renders Korean.
- [ ] 14. `adb logcat -d | grep -i helpkit | tail -100` — zero `AndroidRuntime: FATAL EXCEPTION`. Zero lines containing system-prompt fingerprint phrases.
- [ ] 15. In the app, tap Clear. Transcript empties. Re-run the sqlite3 query — message row count drops to 0 for current user.

**Sign-off:** initials + UTC date.
