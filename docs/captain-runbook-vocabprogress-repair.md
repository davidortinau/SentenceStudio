# Captain Runbook — Repair tainted VocabularyProgress rows on this Mac

**When:** Run this once, manually, when you're back at the keyboard.
**Why:** All 1745 rows of `VocabularyProgress` in your local Mac Catalyst SQLite have literal column NAMES stored as values (`UserDeclaredAt='UserDeclaredAt'`, etc.). EF can't `DateTime.Parse('UserDeclaredAt')`, so every vocab page on Mac Catalyst raises a toast and fails.

This runbook is the **immediate unblock** so you can keep using your Mac Catalyst app today. The permanent code-side guard ships in a separate PR.

## Step 1 — Quit the Mac Catalyst app

Cmd-Q SentenceStudio from the Dock. The DB must be closed before mutating it.

```bash
ps -ef | grep -i sentencestudio | grep -v grep
# If you see a SentenceStudio process, quit it from the Dock first.
```

## Step 2 — Back up the DB

```bash
DB="$HOME/Library/Containers/C5750C50-4CF5-448D-8B79-E5695B8EE653/Data/Library/sstudio.db3"
cp -a "$DB" "$DB.bak-$(date +%Y%m%d-%H%M%S)"
ls -la "$DB"*
```

## Step 3 — Verify the taint count (read-only)

```bash
sqlite3 "$DB" "SELECT COUNT(*) AS tainted FROM VocabularyProgress WHERE UserDeclaredAt = 'UserDeclaredAt';"
# Expected: 1745
```

## Step 4 — Run the repair (idempotent)

```bash
sqlite3 "$DB" <<SQL
UPDATE VocabularyProgress SET UserDeclaredAt    = NULL WHERE UserDeclaredAt    = 'UserDeclaredAt';
UPDATE VocabularyProgress SET VerificationState = 0    WHERE VerificationState = 'VerificationState';
UPDATE VocabularyProgress SET IsUserDeclared    = 0    WHERE IsUserDeclared    = 'IsUserDeclared';
SELECT 'after', COUNT(*) AS still_tainted_userdec
FROM VocabularyProgress WHERE UserDeclaredAt = 'UserDeclaredAt';
SQL
# Expected last line: after|0
```

## Step 5 — Relaunch the app and verify

- Open the Mac Catalyst app.
- Navigate to Vocabulary. **No toast.**
- Try the Vocab Quiz. **Should launch.**
- Watch the Aspire `SentenceStudio.Api` logs for sync uploads — the stuck batch `1c18d82b-d160-424d-9ecf-bec51d04ba1e` should NOT recur.

## If something goes wrong

Restore from the backup created in Step 2:

```bash
cp -a "$DB.bak-<timestamp>" "$DB"
```

## Permanent fix

A separate PR adds `RepairTaintedVocabularyProgressAsync` to `SyncService.cs` that runs this same repair automatically on app startup before `MigrateAsync`, so any other client that ever picks up tainted rows from sync self-heals. See the PR for details.
