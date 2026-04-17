# Sync and Offline

SentenceStudio uses CoreSync to keep mobile and desktop clients aligned with the server-hosted database. Most of the app works offline; sync reconciles changes when the network returns.

## How sync works

- Every write (vocabulary edit, completed activity, new resource) is queued locally.
- When connectivity is available, the client pushes queued changes to the API and pulls server-side changes.
- Conflicts are resolved by last-writer-wins with timestamps captured at write time.

## When sync runs

- On app startup, after the local database has initialized.
- When the device regains internet connectivity.
- After you explicitly trigger a sync from Settings.

## Offline behavior

- Activities that do not call the AI model run offline using cached content.
- AI-powered activities (Cloze generation, translation feedback, conversation) require a live connection because the model runs on the server.
- Vocabulary review and history browsing work fully offline.

## Troubleshooting

If data looks stale:

1. Confirm the device is online.
2. Trigger a manual sync from Settings.
3. Check the sync log for errors — a persistent error usually means the auth token expired; sign in again to refresh.
