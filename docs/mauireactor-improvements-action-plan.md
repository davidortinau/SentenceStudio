# MauiReactor Codebase Improvements Action Plan

**Generated:** 2025-12-19  
**Status:** Review and prioritize

---

## Executive Summary

The codebase follows MauiReactor patterns well overall. Shell navigation is correctly implemented, icons use the centralized theme, and no legacy layout patterns were found. However, there are **critical layout issues** and **widespread accessibility concerns** that need immediate attention.

---

## 🔴 CRITICAL - Fix Immediately

### 1. CollectionView/ScrollView in Unbounded Containers

These cause infinite rendering or broken scrolling.

| File | Line | Issue |
|------|------|-------|
| `DescribeAScenePage.cs` | 83-104 | `CollectionView` inside `VStack` |
| `EditLearningResourcePage.cs` | 293-314 | `CollectionView` inside `VStack` with padding |
| `DescribeAScenePage.cs` | 195-203 | Gallery `CollectionView` in `SfBottomSheet` (potential conflict) |

**Fix:** Wrap in `Grid(rows: "Auto,*", ...)` where the `CollectionView` is in a star-sized row.

```csharp
// ❌ WRONG
VStack(
    Label("Header"),
    CollectionView().ItemsSource(items)
)

// ✅ CORRECT
Grid(rows: "Auto,*", columns: "*",
    Label("Header").GridRow(0),
    CollectionView().ItemsSource(items).GridRow(1)
)
```

---

### 2. Hard-coded Colors for Text (Accessibility Issue)

**100+ instances** of hard-coded colors used for text readability. This breaks accessibility and theme support.

#### Most Common Violations

| Color | Count | Files (Sample) |
|-------|-------|----------------|
| `Colors.Gray` | 35+ | PracticeStreakCard, VocabProgressCard, OnboardingPage, UserProfilePage |
| `Colors.DarkGray` | 7 | ListLearningResourcesPage, ShadowingPage |
| `Colors.Black` | 8 | HowDoYouSayPage, ImageGalleryBottomSheet, ClozurePage |
| `Colors.White` | 20+ | MinimalPairsPage, VocabularyQuizPage, ClozurePage, YouTubeImportPage |

**Fix:** Use theme-aware colors:

```csharp
// ❌ WRONG
.TextColor(Colors.Gray)
.TextColor(Colors.White)

// ✅ CORRECT
.TextColor(MyTheme.SecondaryText)  // For gray captions
.TextColor(MyTheme.LightOnDarkBackground)  // For white on dark backgrounds
```

#### Files Requiring Immediate Attention

1. **OnboardingPage.cs** - Lines 366, 389, 414, 444 (5 instances)
2. **UserProfilePage.cs** - Lines 147, 168, 185, 199, 218 (5 instances)
3. **ListLearningResourcesPage.cs** - Lines 81, 137, 141, 146, 153, 170 (6 instances)
4. **PracticeStreakCard.cs** - Lines 43, 55, 57, 59, 86, 99 (6 instances)
5. **ShadowingPage.cs** - Lines 229, 283, 783, 787 (4 instances)

---

## 🟠 HIGH - Fix Soon

### 3. Missing ThemeKey Usage for Typography

Pages use hard-coded `FontSize()` instead of semantic typography tokens.

| File | Lines | Issue |
|------|-------|-------|
| `VocabularyQuizPage.cs` | 210-290 | `FontSize(64)`, `FontSize(32)` |
| `ClozurePage.cs` | 177 | `FontSize(64)` |
| `ListLearningResourcesPage.cs` | 137 | `FontSize(12)` |
| `OnboardingPage.cs` | 55, 57, 59 | `FontSize(9)` |

**Fix:** Use ThemeKey tokens:

```csharp
// ❌ WRONG
Label("Text").FontSize(64)

// ✅ CORRECT
Label("Text").ThemeKey(MyTheme.Display)  // or LargeTitle, Title1, etc.
```

---

### 4. HorizontalOptions/VerticalOptions Instead of Fluent Methods

5 instances found in 2 files.

| File | Line | Current | Should Be |
|------|------|---------|-----------|
| `VocabProgressCard.cs` | 75 | `HorizontalOptions = LayoutOptions.Center` | `.HCenter()` |
| `VocabProgressCard.cs` | 76 | `VerticalOptions = LayoutOptions.Center` | `.VCenter()` |
| `VocabProgressCard.cs` | 84 | `HorizontalOptions = LayoutOptions.Center` | `.HCenter()` |
| `VocabProgressCard.cs` | 91 | `HorizontalOptions = LayoutOptions.Center` | `.HCenter()` |
| `UserProfilePage.cs` | 173 | `.HorizontalOptions(...)` | `.HStart()` or `.HFill()` |

---

### 5. String-Based ThemeKey References

Use constants for compile-time type safety.

```csharp
// ❌ WRONG
.ThemeKey("Secondary")
.ThemeKey("LargeTitle")

// ✅ CORRECT
.ThemeKey(MyTheme.Secondary)
.ThemeKey(MyTheme.LargeTitle)
```

---

## 🟡 MEDIUM - Cleanup

### 6. Add Missing Theme Color Tokens

Currently missing semantic color tokens that would reduce hard-coded colors:

```csharp
// Add to ApplicationTheme.Colors.cs
public static Color SecondaryText => IsLightTheme ? Gray600 : Gray400;
public static Color CaptionText => IsLightTheme ? Colors.Gray : Colors.LightGray;
public static Color BorderLight => IsLightTheme ? Colors.LightGray : Colors.DimGray;
public static Color StatusSuccess => Colors.Green;
public static Color StatusWarning => Colors.Orange;
public static Color StatusError => Colors.Red;
```

### 7. Acceptable Uses of `Colors.*`

These are generally acceptable and don't need changes:

- `Colors.Transparent` for transparent backgrounds
- `Colors.*` in control defaults (WaveformDrawable, WaveformView)
- `Colors.*` in theme-conditional expressions (`Theme.IsLightTheme ? X : Y`)

---

## ✅ What's Working Well

| Area | Status | Notes |
|------|--------|-------|
| Shell Navigation | ✅ Perfect | All 37+ navigation calls use `Shell.GoToAsync()` |
| Icon Centralization | ✅ Perfect | All icons defined in `ApplicationTheme.Icons.cs` |
| No Legacy Navigation | ✅ Perfect | No `Navigation.PushAsync/PopAsync` found |
| No FillAndExpand | ✅ Perfect | No legacy `LayoutOptions.FillAndExpand` |
| No Wrapper Anti-patterns | ✅ Perfect | No `VStack(Render()).Padding()` patterns |
| Route Registration | ✅ Perfect | AppShell uses proper route patterns |

---

## Recommended Execution Order

1. **Week 1:** Fix critical layout issues (CollectionView in VStack)
2. **Week 2:** Add missing theme tokens (SecondaryText, BorderLight, etc.)
3. **Week 3:** Replace hard-coded `Colors.*` in high-traffic pages (Dashboard, Onboarding)
4. **Week 4:** Fix HorizontalOptions/VerticalOptions and string ThemeKey references
5. **Ongoing:** Address remaining hard-coded colors during regular maintenance

---

## Files by Priority

### Immediate Attention Required
- `src/SentenceStudio/Pages/Scene/DescribeAScenePage.cs`
- `src/SentenceStudio/Pages/LearningResources/EditLearningResourcePage.cs`

### High Priority
- `src/SentenceStudio/Pages/Onboarding/OnboardingPage.cs`
- `src/SentenceStudio/Pages/Account/UserProfilePage.cs`
- `src/SentenceStudio/Pages/LearningResources/ListLearningResourcesPage.cs`
- `src/SentenceStudio/Pages/Dashboard/VocabProgressCard.cs`
- `src/SentenceStudio/Pages/Dashboard/PracticeStreakCard.cs`

### Medium Priority
- `src/SentenceStudio/Pages/VocabularyQuiz/VocabularyQuizPage.cs`
- `src/SentenceStudio/Pages/Shadowing/ShadowingPage.cs`
- `src/SentenceStudio/Pages/Clozure/ClozurePage.cs`
- `src/SentenceStudio/Pages/VocabularyManagement/VocabularyManagementPage.cs`
