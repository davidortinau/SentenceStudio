# Local-dev test accounts

SentenceStudio seeds stable development-only accounts so Captain and agents do not keep creating one-off accounts like `flutter-e2e-{guid}@test.local` while testing local AppHost builds. These accounts are fixtures for local development only.

| Purpose | Email | Password | Display name |
|---|---|---|---|
| Captain's daily-driver | `captain@test.local` | `Captain1!` | Captain |
| iOS auto-login | `testsailor@test.local` | `TestPass123!` | Test Sailor |
| E2E / scripts | `e2e@test.local` | `E2E1234!` | E2E Tester |

All three accounts are seeded with `EmailConfirmed=true` and a linked `UserProfile` using `NativeLanguage=English`, `TargetLanguage=Korean`, and `TargetCEFRLevel=A1`.

## Source of truth

The fixture list lives in `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs`. The seeder runs only for the API in Development and is idempotent on AppHost startup.

## Adding a new fixture

Update these files together:

1. `src/SentenceStudio.Api/Auth/DevTestAccountSeeder.cs`
2. `docs/local-dev-test-accounts.md`
3. `.github/copilot-instructions.md`

Keep fixture emails clearly local, such as `name@test.local`. Do not add real-looking production emails or production-like passwords.

## Rotation policy

These passwords are not secrets; they are development-only fixtures. Never reuse them for production, demos backed by production data, or any external service. If a fixture password needs to change, update the seeder, this doc, and Copilot instructions in the same commit.

## Testing against an established database instead

Seeded fixtures start empty of real learning history. When you need a worktree to have vocabulary,
plans, and progress — for Today's Plan, progress, or Learning Coach work — clone an established
local database volume instead of seeding by hand. See `docs/local-dev-database-volumes.md`.
