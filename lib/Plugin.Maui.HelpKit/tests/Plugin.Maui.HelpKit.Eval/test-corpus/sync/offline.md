# Sync and offline behavior

SentenceStudio stores your data locally in SQLite and syncs with the hosted backend when a network connection is available.

## Offline

- Activities that do not require AI grading work offline using cached content.
- Writing and Translation grading require the AI service and will queue or fail gracefully when offline.
- Your vocabulary, progress records, and resources remain available offline.

## Sync

When you reconnect, pending progress and vocabulary changes sync to the backend. There is no manual sync button in the Alpha; sync happens automatically on app launch and when network status changes.
