# Channel Monitoring Investigation — Session Log

**Date:** 2026-03-21T01:13Z  
**Participants:** Wash (Backend Dev)  
**Topic:** YouTube channel URL monitoring feasibility without OAuth

## Summary

Wash investigated full feasibility of YouTube channel monitoring via YoutubeExplode. Result: **Ready to build**. YoutubeExplode v6.5.6 (already installed) handles channel URL resolution, video enumeration, date filtering, and transcript extraction. No new packages needed. Simplified approach cuts OAuth/webhooks complexity by ~80%.

## Key Findings

- Channel handle resolution: `GetByHandleAsync("https://youtube.com/@channel_name")`
- Video enumeration: `GetUploadsAsync()` returns newest-first (natural dedup ordering)
- Date filtering: Fetch full video metadata (one call per video) to check `UploadDate`
- Transcripts: Existing `YouTubeImportService` already extracts Korean captions

## Decisions Made

1. Two-phase worker architecture (polling + ingestion separation)
2. `MonitoredChannel` + `VideoImport` entities (server-only, non-synced)
3. 6-hour poll interval for <10 channels
4. Dedup via `VideoImport.VideoId` unique key

## Effort & Timeline

- **Core implementation:** 2-3 days (models + migration + workers + API)
- **UI:** Separate track, additional design needed
- **Blockers:** None identified

## Decision Doc

Written to: `.squad/decisions/inbox/wash-channel-monitoring.md` (411 lines, comprehensive)
