# Session: Starter Resource Duplicate ID Fix — 2026-03-28T02:25

**Agent:** Wash (Backend Dev)  
**Status:** ✅ COMPLETE

## Problem
PostgreSQL 23505 unique_violation crash when saving duplicate starter resource IDs.

## Solution
- Cleared Vocabulary navigation property before Add() in SaveResourceAsync
- Added StarterResourceExistsAsync duplicate guard
- Improved error messages

## Files Changed
- src/SentenceStudio.Shared/Data/LearningResourceRepository.cs
- src/SentenceStudio.UI/Pages/Index.razor
- src/SentenceStudio.UI/Pages/Resources.razor

## Outcome
✅ Build passes clean. Duplicate ID constraint now enforced. EF Core cascade-insert bug fixed.
