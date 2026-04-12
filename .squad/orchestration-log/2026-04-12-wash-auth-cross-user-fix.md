# Orchestration: Wash — Cross-User Data Leak Fix (IActiveUserProvider)

**Date:** 2025-07-18  
**Spawn:** wash-auth-cross-user-fix  
**Mode:** Background  
**Charter:** Backend & Integration Developer  

## Task

Fix CRITICAL security bug: cross-user data leak where all server users saw the last-login user's profile.

## Status

✅ COMPLETED

## Problem

`WebPreferencesService` is a server-side singleton backed by a single JSON file. It stored `active_profile_id` globally, shared across ALL authenticated users. Multi-user invariant violated: "whoever logs in last overwrites everyone's active profile."

Root cause: Preference system designed for single-device MAUI; broken on multi-user server.

## Solution

**IActiveUserProvider** — host-aware abstraction with two implementations:

1. **MAUI** (PreferencesActiveUserProvider)
   - Reads from device preferences
   - Single user per device → safe

2. **WebApp** (ClaimsActiveUserProvider)
   - Reads from authenticated user's Identity claims via IHttpContextAccessor
   - Returns user's profile from UserManager<ApplicationUser>.UserProfileId
   - Never touches shared JSON file for user identity

Interface also exposes `ShouldFallbackToFirstProfile`:
- MAUI: true (safe — one device)
- WebApp: false (critical — prevents random profile selection)

## DI Registration

- WebApp registers ClaimsActiveUserProvider in Program.cs BEFORE AddSentenceStudioCoreServices()
- CoreServiceExtensions uses TryAddSingleton<IActiveUserProvider, PreferencesActiveUserProvider>()
- WebApp's registration wins; MAUI gets preferences-based default

## Files Changed

**Created:**
- IActiveUserProvider.cs
- PreferencesActiveUserProvider.cs
- WebApp/Auth/ClaimsActiveUserProvider.cs

**Modified (all 8 now use IActiveUserProvider):**
- UserProfileRepository.cs
- SkillProfileRepository.cs
- UserActivityRepository.cs
- VocabularyProgressRepository.cs
- LearningResourceRepository.cs
- ProgressCacheService.cs
- WebApp/Program.cs
- CoreServiceExtensions.cs

## Remaining Work

Blazor pages (VocabQuiz, Writing, Import, etc.) still read active_profile_id directly from IPreferencesService. Should migrate to IActiveUserProvider in follow-up. Critical data-layer leak now plugged.

## Decision Record

Merged to decisions.md: Decision: IActiveUserProvider abstraction to fix cross-user data leak
