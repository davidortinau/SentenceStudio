# Test Accounts

Canonical test accounts for Squad agents (Jayne, Kaylee, Wash, etc.) to use during E2E verification. **DO NOT delete or change these without coordinating** — agents look here for credentials and re-creating them across environments wastes cycles.

These accounts must be created in each new environment (fresh local DB, fresh sim, fresh device). The account creation flow registers them in whichever DB the app is currently pointed at (local Aspire Postgres, local SQLite on a sim/device, Azure prod, etc.). They are **test accounts**, never production data.

> ⚠️ Never commit real Captain creds to this file. Real Captain creds live in his Keychain.

---

## squad-jayne (primary E2E test account)

| Field | Value |
|-------|-------|
| **Email** | `squad-jayne@sentencestudio.test` |
| **Password** | `SquadTest!2026` |
| **Display Name** | Jayne Test |
| **Native Language** | English |
| **Target Language** | Korean |
| **Owner** | Jayne (Tester) |
| **Use** | Primary E2E account on webapp + iOS Sim + Mac Catalyst |

If a sim/environment has never been registered, Jayne creates this account via the Register flow on first run and confirms it works for subsequent E2E sessions.

---

## squad-kaylee (frontend smoke / register-flow tests)

| Field | Value |
|-------|-------|
| **Email** | `squad-kaylee@sentencestudio.test` |
| **Password** | `SquadTest!2026` |
| **Display Name** | Kaylee Test |
| **Native Language** | English |
| **Target Language** | Korean |
| **Owner** | Kaylee (Full-stack Dev) |
| **Use** | Optional secondary — only if Jayne's account is in a state that would interfere with the test |

---

## How to use

When verifying a feature on a fresh environment:

1. Look here first — these are the accounts to use, do not invent new ones.
2. If the account doesn't exist on the target environment yet, register it via the app's standard Register flow.
3. Note the environment in your decision-file output (e.g., "verified on iOS Sim 17 Pro / 26.2 with squad-jayne").
4. If the password is rejected by a stronger validation policy in the future, update this file (and bump the password — keep the format `SquadTest!{year}` or similar memorable pattern).

