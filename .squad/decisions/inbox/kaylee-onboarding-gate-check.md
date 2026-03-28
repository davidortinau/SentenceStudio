# Decision: Onboarding gate requires all three profile fields

**Author:** Kaylee (Full-stack Dev)
**Date:** 2025-07-15
**Status:** Implemented

## Context
The `is_onboarded` preference controls whether users are redirected to `/onboarding`. Multiple code paths were setting this flag too eagerly — before the user's profile had all required fields (TargetLanguage, NativeLanguage, Name).

## Decision
Any code path that sets `is_onboarded = true` MUST first verify the profile has all three fields populated:
- `TargetLanguage`
- `NativeLanguage`  
- `Name`

If any are missing, the user should be redirected to `/onboarding` rather than granted dashboard access.

## Affected Files
- `Layout/MainLayout.razor` — existing-profile bypass check
- `Pages/Auth.razor` — local profile login
- `Pages/LoginPage.razor` — post-login redirect
- `Pages/Index.razor` — starter content creation uses user's language

## Rationale
Onboarding collects these three fields in a guided flow. Bypassing it with incomplete data leads to broken language selection, hardcoded defaults, and a confusing UX.
