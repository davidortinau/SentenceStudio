# Route Storage Cleanup - Migration Summary

## 🏴‍☠️ Problem Identified

The `DailyPlanCompletion` table was storing redundant route information:
- `Route` (e.g., "/reading") 
- `RouteParametersJson` (e.g., `{"ResourceId":25,"SkillId":1}`)

This information was **redundant** because it could be derived from:
- `ActivityType` → determines route via `PlanConverter.GetRouteForActivity()`
- `ResourceId` + `SkillId` → determines parameters via `PlanConverter.BuildRouteParameters()`

## ✅ Changes Made

### 1. Model Changes
**File**: `src/SentenceStudio.Shared/Models/DailyPlanCompletion.cs`
- ❌ Removed: `Route` property
- ❌ Removed: `RouteParametersJson` property
- ✅ Added documentation explaining routes are derived, not stored

### 2. PlanConverter Updates
**File**: `src/SentenceStudio/Services/PlanGeneration/PlanConverter.cs`
- Made `GetRouteForActivity()` **public** (was private)
- Created public `BuildRouteParameters(activityType, resourceId, skillId)` overload
- Kept private `BuildRouteParameters(activity, activityType)` wrapper for internal use

### 3. ProgressService Updates
**File**: `src/SentenceStudio/Services/Progress/ProgressService.cs`

**InitializePlanCompletionRecordsAsync** (lines 751-796):
- Removed storing `Route` and `RouteParametersJson` fields
- Now only stores essential data: `ActivityType`, `ResourceId`, `SkillId`

**ReconstructPlanFromDatabase** (lines 802-893):
- ❌ Removed: JSON deserialization of route parameters
- ✅ Added: Dynamic route/parameter derivation using:
  ```csharp
  var route = PlanConverter.GetRouteForActivity(activityType);
  var routeParams = PlanConverter.BuildRouteParameters(activityType, completion.ResourceId, completion.SkillId);
  ```

### 4. Database Migration
**File**: `src/SentenceStudio.Shared/Migrations/20251126160131_RemoveRedundantRouteStorage.cs`
- Drops `Route` column from `DailyPlanCompletion` table
- Drops `RouteParametersJson` column from `DailyPlanCompletion` table
- Includes rollback logic in `Down()` method

## 🎯 Benefits

1. **Single Source of Truth**: Route logic exists only in `PlanConverter`
2. **Adaptability**: If route patterns change, old plans automatically use new logic
3. **Reduced Storage**: Less data stored in database
4. **Maintainability**: No need to keep route mapping in sync across multiple places
5. **Fixed Bug**: The original issue where Reading/Listening used wrong resources is now resolved

## 🧪 Testing Required

1. **Generate a new plan** - verify routes are correctly derived
2. **Restart app** - verify plan reconstruction works from database
3. **Complete activities** - verify progress tracking still works
4. **Navigation** - verify tapping plan items navigates to correct pages with correct resources

## 📦 Migration Application

The migration will run automatically on next app launch. The migration:
- ✅ Is safe - only removes columns that are being regenerated
- ✅ Is reversible - includes Down() method
- ✅ Preserves data - `ActivityType`, `ResourceId`, `SkillId` remain intact

## 🔧 Technical Details

**Route Derivation Logic**:
```csharp
// Example: Reading activity with ResourceId=25, SkillId=1
ActivityType: Reading
ResourceId: 25
SkillId: 1

// Derives to:
Route: "/reading"
RouteParameters: { "ResourceId": 25, "SkillId": 1 }
```

**Why This Works**:
- `PlanConverter.GetRouteForActivity()` maps activity types to routes
- `PlanConverter.BuildRouteParameters()` creates parameters based on activity type
- Both methods use the same enum values, ensuring consistency

## 🏴‍☠️ Captain's Notes

This cleanup be part of the fix for the navigation issue where Today's Plan was loadin' the wrong resource. By derivin' routes from the stored `ActivityType` and `ResourceId`, we ensure the plan always navigates to the correct resource, not the one selected in "Choose My Own" mode.

Arrr! Much cleaner seas ahead! 🌊
