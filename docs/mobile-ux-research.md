# Mobile UX Research for Blazor Hybrid

**Research Date:** March 2026  
**Researcher:** Kaylee (Full-stack Dev)  
**Context:** SentenceStudio - .NET MAUI Blazor Hybrid language learning app

## Executive Summary

This research investigates mobile-native UX patterns for Blazor Hybrid applications, focusing on infinite scrolling, virtualization, pull-to-refresh, and touch interactions. Key findings:

1. **Blazor's `<Virtualize>` component works in Blazor Hybrid** and is the recommended approach for large lists (2000-3000 items). It only renders visible items and handles variable heights automatically. For SentenceStudio's vocabulary lists, loading all data into memory and virtualizing is the best approach.

2. **Pull-to-refresh is challenging in Blazor Hybrid**. Native `RefreshView` cannot wrap `BlazorWebView` due to the webview architecture. A JavaScript-based solution using CSS `overscroll-behavior` and touch events is feasible but complex. For MVP, recommend skipping pull-to-refresh and using a refresh button instead.

3. **Mobile touch patterns require JavaScript interop**. Swipe gestures, haptic feedback, and bottom sheets need JS + native API calls. Platform detection using `DeviceInfo` allows conditional rendering for mobile vs web UX.

4. **Infinite scroll pattern** is best implemented with `IntersectionObserver` API (JS interop) to detect when user scrolls near the bottom, then fetch more items. However, for lists where search/filter operates on the full dataset, `<Virtualize>` with all data loaded is cleaner and performs well.

---

## 1. List Virtualization & Infinite Scroll

### Current State

The app currently uses two patterns for list pagination:

1. **Resources.razor** — Load all resources into memory, use `.Take(displayCount)` to show a subset, render "Show More" button to increase count.
2. **Vocabulary.razor** — Load all vocabulary words, render the full list with `@foreach`, no pagination (potential performance issue for 2000+ items).

Both patterns load **all data** at once, then control display via Take/Skip or full rendering. This is inefficient for large lists and not mobile-native UX.

### Options

#### Option A: Blazor `<Virtualize>` Component (Recommended)

**How it works:**
- Blazor's built-in `<Virtualize>` component only renders items currently visible in the viewport.
- Automatically calculates which items to render based on scroll position.
- Supports two modes:
  - **Items collection**: Pass the entire collection, component handles rendering visible subset.
  - **ItemsProvider delegate**: Fetch data on-demand as user scrolls (true infinite scroll).

**Variable-height items:**
- Use `ItemSize` parameter as an **estimate** (average height).
- Blazor measures actual rendered items and adjusts calculations dynamically.
- For widely varying heights, performance may degrade slightly but still better than rendering all items.

**Search/filter with Virtualize:**
- When using `Items` mode, you maintain the full dataset in memory.
- Apply search/filter to the full list, pass the filtered list to `<Virtualize Items="filteredList">`.
- Search operates on the entire dataset (not just rendered items), which is correct UX.

**Code Pattern:**

```razor
<div style="height: 70vh; overflow-y: auto;">
    <Virtualize Items="filteredWords" Context="word">
        <VocabularyCard Word="@word" OnEdit="EditWord" />
    </Virtualize>
</div>

@code {
    private List<VocabularyWord> allWords = new();
    private List<VocabularyWord> filteredWords = new();
    
    protected override async Task OnInitializedAsync()
    {
        allWords = await VocabRepo.GetAllAsync();
        filteredWords = allWords;
    }
    
    private void ApplyFilter(string query)
    {
        filteredWords = allWords
            .Where(w => w.Term.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
```

**Pros:**
- Built into Blazor, no additional packages or JS interop.
- Works in Blazor Hybrid (tested by Microsoft).
- Handles variable-height items automatically.
- Search/filter logic is simple (filter the full list, pass to Virtualize).
- Performance: Easily handles 10,000+ items.

**Cons:**
- Requires fixed-height container (`overflow-y: auto`).
- `ItemSize` estimate affects initial render accuracy (but auto-corrects).
- Keyboard accessibility requires container to be focusable (`tabindex="-1"`).

#### Option B: ItemsProvider Delegate (True Infinite Scroll)

For scenarios where loading all data upfront is impractical (e.g., 100,000+ records), use `ItemsProvider` to fetch data on demand:

```razor
<Virtualize Context="word" ItemsProvider="LoadWords">
    <VocabularyCard Word="@word" />
</Virtualize>

@code {
    private async ValueTask<ItemsProviderResult<VocabularyWord>> LoadWords(
        ItemsProviderRequest request)
    {
        // Fetch only the requested range from database
        var words = await VocabRepo.GetRangeAsync(
            skip: request.StartIndex, 
            take: request.Count ?? 50);
        
        var totalCount = await VocabRepo.GetTotalCountAsync();
        
        return new ItemsProviderResult<VocabularyWord>(words, totalCount);
    }
}
```

**Search/filter challenge:**
- When using `ItemsProvider`, search must query the **database**, not the in-memory list.
- Pass filter parameters to the repository method.
- This requires more complex backend logic but scales to millions of records.

#### Option C: JavaScript IntersectionObserver (Manual Infinite Scroll)

If you need more control than `<Virtualize>` provides, implement infinite scroll manually:

```razor
<div @ref="scrollContainer" style="height: 70vh; overflow-y: auto;">
    @foreach (var word in displayedWords)
    {
        <VocabularyCard Word="@word" />
    }
    <div @ref="sentinel" style="height: 1px;"></div>
</div>

@code {
    private ElementReference scrollContainer;
    private ElementReference sentinel;
    private List<VocabularyWord> displayedWords = new();
    private int currentPage = 0;
    private const int pageSize = 50;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadNextPage();
            await JS.InvokeVoidAsync("setupIntersectionObserver", 
                sentinel, DotNetObjectReference.Create(this));
        }
    }
    
    [JSInvokable]
    public async Task OnSentinelVisible()
    {
        await LoadNextPage();
    }
    
    private async Task LoadNextPage()
    {
        var nextWords = await VocabRepo.GetPageAsync(currentPage, pageSize);
        displayedWords.AddRange(nextWords);
        currentPage++;
        StateHasChanged();
    }
}
```

**JavaScript (in app.js):**

```javascript
window.setupIntersectionObserver = (sentinel, dotnetHelper) => {
    const observer = new IntersectionObserver((entries) => {
        if (entries[0].isIntersecting) {
            dotnetHelper.invokeMethodAsync('OnSentinelVisible');
        }
    }, { threshold: 0.1 });
    
    observer.observe(sentinel);
};
```

**Pros:**
- Fine-grained control over loading behavior.
- Can add loading spinners, error handling, etc.

**Cons:**
- More complex than `<Virtualize>`.
- Requires JS interop (additional code).
- Search/filter is tricky (need to reset and reload).

### Recommendation

**Use Blazor `<Virtualize>` with Items collection for SentenceStudio.**

**Rationale:**
1. Vocabulary lists are typically 500-5,000 words (manageable in memory).
2. Search/filter needs access to the full dataset for accurate results.
3. `<Virtualize>` is built-in, well-tested, and requires minimal code.
4. For future scale (10,000+ words), switch to `ItemsProvider` mode.

**Implementation Priority:**
1. **Phase 1**: Replace `@foreach` in Vocabulary.razor with `<Virtualize Items="filteredWords">`.
2. **Phase 2**: Apply same pattern to Resources.razor (remove "Show More" button).
3. **Phase 3**: If lists exceed 10,000 items, migrate to `ItemsProvider` with database pagination.

### Code Pattern (Pseudocode)

```razor
@* Vocabulary.razor *@
<div class="vocabulary-list" style="height: calc(100vh - 200px); overflow-y: auto;" tabindex="-1">
    <Virtualize Items="filteredWords" Context="word" ItemSize="80">
        <div class="vocab-card" @onclick="() => EditWord(word.Id)">
            <div class="vocab-term">@word.TargetLanguageTerm</div>
            <div class="vocab-translation">@word.NativeLanguageTerm</div>
            <span class="badge">@word.Status</span>
        </div>
    </Virtualize>
</div>

@code {
    private List<VocabularyWord> allWords = new();
    private List<VocabularyWord> filteredWords = new();
    
    protected override async Task OnInitializedAsync()
    {
        allWords = await VocabRepo.GetAllWordsAsync();
        ApplyFilters(); // Initial filter application
    }
    
    private void ApplyFilters()
    {
        filteredWords = allWords
            .Where(w => /* filter logic based on parsedQuery */)
            .ToList();
        StateHasChanged();
    }
}
```

**Key Points:**
- Container must have fixed height and `overflow-y: auto`.
- Add `tabindex="-1"` for keyboard scroll support.
- `ItemSize="80"` is an estimate (measure your card height).
- Search/filter operates on `allWords`, updates `filteredWords`, Virtualize re-renders visible items.

---

## 2. Pull-to-Refresh

### Options

#### Option A: Native MAUI RefreshView + BlazorWebView

**Feasibility:** **Not possible with current architecture.**

.NET MAUI's `RefreshView` is a native control that wraps content and adds pull-to-refresh gesture handling. However, `BlazorWebView` is the root content control in Blazor Hybrid apps and cannot be wrapped by `RefreshView`.

**Why it doesn't work:**
- In a MAUI Blazor Hybrid app, the layout hierarchy is:
  ```
  ContentPage
    └── BlazorWebView (root content)
          └── WebView (platform-specific)
                └── Blazor components (HTML/CSS/JS)
  ```
- `RefreshView` needs to be an **ancestor** of the scrollable content it monitors.
- `BlazorWebView` is the scroll container and cannot be nested inside `RefreshView`.
- Workarounds (e.g., per-page native controls) break the unified Blazor UI model.

#### Option B: CSS overscroll-behavior + JS Touch Events

**Feasibility:** **Possible but complex.**

Implement pull-to-refresh entirely in web tech using CSS and JavaScript:

**Steps:**
1. Detect touch events (`touchstart`, `touchmove`, `touchend`) at the top of the scroll container.
2. If user is at `scrollTop === 0` and pulls down, show a refresh indicator.
3. If pull distance exceeds threshold, trigger refresh action.
4. Use CSS `overscroll-behavior: contain` to prevent native bounce/overscroll.

**Pseudocode:**

```razor
<div @ref="scrollContainer" class="pull-to-refresh-container">
    <div class="refresh-indicator" style="transform: translateY(@refreshIndicatorOffset);">
        @if (isRefreshing)
        {
            <div class="spinner-border"></div>
        }
        else
        {
            <i class="bi bi-arrow-down-circle"></i>
        }
    </div>
    
    <div class="content">
        @* Your list content *@
    </div>
</div>

@code {
    private ElementReference scrollContainer;
    private bool isRefreshing = false;
    private string refreshIndicatorOffset = "-60px";
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("setupPullToRefresh", 
                scrollContainer, DotNetObjectReference.Create(this));
        }
    }
    
    [JSInvokable]
    public async Task OnRefreshTriggered()
    {
        isRefreshing = true;
        StateHasChanged();
        
        await LoadData();
        
        isRefreshing = false;
        StateHasChanged();
    }
}
```

**JavaScript (in app.js):**

```javascript
window.setupPullToRefresh = (container, dotnetHelper) => {
    let startY = 0;
    let currentY = 0;
    let isPulling = false;
    
    container.addEventListener('touchstart', (e) => {
        if (container.scrollTop === 0) {
            startY = e.touches[0].clientY;
            isPulling = true;
        }
    });
    
    container.addEventListener('touchmove', (e) => {
        if (!isPulling) return;
        currentY = e.touches[0].clientY;
        const pullDistance = currentY - startY;
        
        if (pullDistance > 0 && pullDistance < 100) {
            // Show visual feedback
            e.preventDefault(); // Prevent native scroll
        }
    });
    
    container.addEventListener('touchend', (e) => {
        if (!isPulling) return;
        const pullDistance = currentY - startY;
        
        if (pullDistance > 80) {
            dotnetHelper.invokeMethodAsync('OnRefreshTriggered');
        }
        
        isPulling = false;
    });
};
```

**Pros:**
- Works in Blazor Hybrid (pure web tech).
- Can be customized per page.

**Cons:**
- Complex JS interop and touch event handling.
- Must prevent native scroll carefully (can break UX).
- Platform differences (iOS vs Android touch behavior).
- Doesn't feel as native as platform-provided pull-to-refresh.

#### Option C: Community Packages

**Current state (March 2026):** No mature Blazor Hybrid pull-to-refresh libraries found. Most focus on Blazor WebAssembly/Server (where native gestures aren't relevant).

### Recommendation

**Skip pull-to-refresh for MVP. Use a refresh button instead.**

**Rationale:**
1. Pull-to-refresh is **nice-to-have**, not essential for language learning UX.
2. Implementing it properly requires significant JS interop complexity.
3. Refresh button is clear, accessible, and works consistently across platforms.
4. For mobile, place refresh button in the page header toolbar (already present in PageHeader component).

**Future consideration:**
- If pull-to-refresh becomes a priority, invest in a reusable JS interop module.
- Monitor Blazor Hybrid community for libraries/patterns.

**Current workaround in SentenceStudio:**
- PageHeader already supports toolbar actions (e.g., filter button in Vocabulary.razor).
- Add a refresh icon button that calls `await LoadData()`:

```razor
<PageHeader Title="Vocabulary">
    <ToolbarActions>
        <button class="btn btn-sm btn-icon" @onclick="RefreshData" title="Refresh">
            <i class="bi bi-arrow-clockwise"></i>
        </button>
    </ToolbarActions>
</PageHeader>
```

---

## 3. Mobile Touch Patterns

### Swipe Actions on List Items

**Feasibility:** **Possible with JS interop, but limited in Blazor Hybrid.**

.NET MAUI has a native `SwipeView` control for XAML-based apps, but Blazor Hybrid uses HTML/CSS rendering inside a WebView. You cannot directly use `SwipeView` with Blazor components.

**Options:**

#### Option A: JavaScript Touch Events (Custom Implementation)

Detect horizontal swipe gestures (`touchstart`, `touchmove`, `touchend`) and reveal action buttons:

```javascript
window.setupSwipeActions = (element, dotnetHelper) => {
    let startX = 0;
    let currentX = 0;
    
    element.addEventListener('touchstart', (e) => {
        startX = e.touches[0].clientX;
    });
    
    element.addEventListener('touchmove', (e) => {
        currentX = e.touches[0].clientX;
        const swipeDistance = startX - currentX;
        
        if (swipeDistance > 0) {
            // Swipe left: reveal delete button
            element.style.transform = `translateX(-${Math.min(swipeDistance, 80)}px)`;
        }
    });
    
    element.addEventListener('touchend', (e) => {
        const swipeDistance = startX - currentX;
        
        if (swipeDistance > 50) {
            // Trigger delete action
            dotnetHelper.invokeMethodAsync('OnSwipeDelete', element.dataset.itemId);
        } else {
            // Reset position
            element.style.transform = 'translateX(0)';
        }
    });
};
```

**Pros:**
- Full control over swipe behavior.
- Can customize animation, thresholds, actions.

**Cons:**
- Significant JS interop code.
- Must handle edge cases (multi-touch, scroll conflicts, etc.).
- Platform differences in touch behavior.

#### Option B: CSS :active + Long Press (Simplified)

For mobile, consider **long press** instead of swipe as an alternative pattern:

```razor
<div class="vocab-card" @onclick="EditWord" @oncontextmenu:preventDefault @oncontextmenu="ShowActions">
    @* Card content *@
</div>

@code {
    private void ShowActions(MouseEventArgs e)
    {
        // Show action sheet or modal with delete/edit options
    }
}
```

**Note:** `@oncontextmenu` fires on long press in mobile browsers.

#### Recommendation

**Use long-press + action sheet instead of swipe for MVP.**

**Rationale:**
1. Long press is easier to implement (no JS interop needed).
2. Action sheets are a standard mobile pattern (iOS/Android).
3. Swipe actions require significant engineering effort for a single feature.
4. Bootstrap modals or offcanvas can serve as action sheets.

**Implementation:**

```razor
@foreach (var word in words)
{
    <div class="vocab-card" 
         @onclick="() => EditWord(word.Id)" 
         @oncontextmenu:preventDefault
         @oncontextmenu="() => ShowActionSheet(word)">
        @* Card content *@
    </div>
}

@* Action sheet (Bootstrap offcanvas) *@
<div class="offcanvas offcanvas-bottom" id="actionSheet" tabindex="-1">
    <div class="offcanvas-body">
        <button class="btn btn-danger w-100 mb-2" @onclick="DeleteWord">Delete</button>
        <button class="btn btn-secondary w-100" data-bs-dismiss="offcanvas">Cancel</button>
    </div>
</div>

@code {
    private VocabularyWord? selectedWord;
    
    private async Task ShowActionSheet(VocabularyWord word)
    {
        selectedWord = word;
        await JS.InvokeVoidAsync("bootstrap.Offcanvas.getOrCreateInstance", 
            document.getElementById("actionSheet")).show();
    }
}
```

### Bottom Sheets / Action Sheets

**Current state:** SentenceStudio already uses Bootstrap offcanvas for mobile-only filter panels (Vocabulary.razor).

**Pattern:**
- Use `offcanvas offcanvas-bottom` with `d-md-none` for mobile-only sheets.
- Desktop shows modals or inline actions.

**Example:**

```razor
@* Mobile: Offcanvas bottom sheet *@
<div class="offcanvas offcanvas-bottom d-md-none" id="mobileActions">
    <div class="offcanvas-body">
        <button class="btn btn-primary w-100 mb-2" @onclick="Action1">Action 1</button>
        <button class="btn btn-secondary w-100 mb-2" @onclick="Action2">Action 2</button>
        <button class="btn btn-outline-secondary w-100" data-bs-dismiss="offcanvas">Cancel</button>
    </div>
</div>

@* Desktop: Dropdown menu *@
<div class="dropdown d-none d-md-block">
    <button class="btn btn-secondary dropdown-toggle" data-bs-toggle="dropdown">Actions</button>
    <ul class="dropdown-menu">
        <li><button class="dropdown-item" @onclick="Action1">Action 1</button></li>
        <li><button class="dropdown-item" @onclick="Action2">Action 2</button></li>
    </ul>
</div>
```

**Recommendation:** Continue using Bootstrap offcanvas for action sheets. This is the established pattern in the app.

### Haptic Feedback

**Feasibility:** **Yes, via JS interop to .NET MAUI APIs.**

.NET MAUI provides `HapticFeedback` API:

```csharp
// In a Blazor component code-behind or service
HapticFeedback.Perform(HapticFeedbackType.Click);
HapticFeedback.Perform(HapticFeedbackType.LongPress);
```

**Integration with Blazor:**
1. Create a C# service that wraps `HapticFeedback`:

```csharp
// In MAUI project
public interface IHapticService
{
    void Perform(HapticFeedbackType type);
    bool IsSupported { get; }
}

public class HapticService : IHapticService
{
    public bool IsSupported => HapticFeedback.Default.IsSupported;
    
    public void Perform(HapticFeedbackType type)
    {
        if (IsSupported)
            HapticFeedback.Default.Perform(type);
    }
}

// Register in MauiProgram.cs
builder.Services.AddSingleton<IHapticService, HapticService>();
```

2. Inject into Blazor components:

```razor
@inject IHapticService Haptics

<button @onclick="HandleClick">Tap Me</button>

@code {
    private void HandleClick()
    {
        Haptics.Perform(HapticFeedbackType.Click);
        // Handle action...
    }
}
```

**Use cases:**
- Button taps (especially for important actions like "Delete").
- Quiz answer feedback (correct = short vibration, wrong = longer pattern).
- Pull-to-refresh trigger (if implemented).

**Recommendation:** Add `IHapticService` wrapper and use for key interactions (quiz grading, word deletion).

### Skeleton Loading

**Feasibility:** **Yes, pure CSS/HTML in Blazor.**

Skeleton screens show placeholder content while data loads (better UX than spinners).

**Implementation:**

```razor
@if (isLoading)
{
    @* Skeleton placeholders *@
    @for (int i = 0; i < 10; i++)
    {
        <div class="vocab-card skeleton">
            <div class="skeleton-text" style="width: 60%;"></div>
            <div class="skeleton-text" style="width: 80%;"></div>
        </div>
    }
}
else
{
    @* Actual data *@
    @foreach (var word in words)
    {
        <VocabularyCard Word="@word" />
    }
}
```

**CSS:**

```css
.skeleton {
    animation: pulse 1.5s ease-in-out infinite;
    background: linear-gradient(90deg, #f0f0f0 25%, #e0e0e0 50%, #f0f0f0 75%);
    background-size: 200% 100%;
}

.skeleton-text {
    height: 1rem;
    border-radius: 4px;
    margin-bottom: 0.5rem;
}

@keyframes pulse {
    0% { background-position: 200% 0; }
    100% { background-position: -200% 0; }
}
```

**Recommendation:** Add skeleton loading to Vocabulary and Resources pages. It's a quick win for perceived performance.

### Smooth Transitions

**Feasibility:** **Yes, CSS transitions + Blazor's built-in animations.**

**Page transitions:**
- Blazor doesn't have built-in page transition animations (unlike native frameworks).
- Use CSS transitions on the main content container:

```css
.main-content {
    transition: opacity 0.2s ease-in-out;
}

.page-enter {
    opacity: 0;
}

.page-enter-active {
    opacity: 1;
}
```

**List item animations:**
- Use CSS transitions for hover/active states:

```css
.vocab-card {
    transition: transform 0.15s ease, box-shadow 0.15s ease;
}

.vocab-card:active {
    transform: scale(0.98);
    box-shadow: 0 1px 3px rgba(0,0,0,0.1);
}
```

**Recommendation:** Add subtle transitions to interactive elements. Keep animations fast (<200ms) for mobile.

---

## 4. Platform-Conditional Rendering

### Current Detection

The app uses `DeviceInfo.Platform` for platform detection (found in `Index.razor`):

```csharp
if (SyncService != null && DeviceInfo.Platform != DevicePlatform.Unknown)
{
    Logger.LogInformation("Platform: {Platform}", DeviceInfo.Platform);
}
```

**Available platforms:**
- `DevicePlatform.Android`
- `DevicePlatform.iOS`
- `DevicePlatform.MacCatalyst`
- `DevicePlatform.WinUI`
- `DevicePlatform.Unknown` (when running as Blazor Server web app)

### Recommendation

**Use a combination of C# platform checks and CSS media queries:**

#### Pattern 1: C# Conditional Rendering

Create a platform detection service:

```csharp
// Services/PlatformService.cs
public interface IPlatformService
{
    bool IsMobile { get; }
    bool IsWeb { get; }
    DevicePlatform Platform { get; }
}

public class PlatformService : IPlatformService
{
    public bool IsMobile => Platform == DevicePlatform.Android || 
                            Platform == DevicePlatform.iOS;
    
    public bool IsWeb => Platform == DevicePlatform.Unknown;
    
    public DevicePlatform Platform => DeviceInfo.Current.Platform;
}

// Register in both MauiProgram.cs and WebApp Program.cs
builder.Services.AddSingleton<IPlatformService, PlatformService>();
```

**Usage in components:**

```razor
@inject IPlatformService Platform

@if (Platform.IsMobile)
{
    @* Infinite scroll with Virtualize *@
    <Virtualize Items="words" Context="word">
        <VocabularyCard Word="@word" />
    </Virtualize>
}
else
{
    @* Desktop: Paginated table *@
    <table>
        @foreach (var word in currentPageWords)
        {
            <tr><td>@word.Term</td></tr>
        }
    </table>
    <Pagination />
}
```

#### Pattern 2: CSS Media Queries (Preferred for Most Cases)

Use Bootstrap's responsive classes (`d-md-none`, `d-none d-md-block`) to conditionally show/hide elements:

```razor
@* Mobile: Show filter icon in toolbar *@
<button class="btn btn-icon d-md-none" data-bs-toggle="offcanvas" data-bs-target="#filters">
    <i class="bi bi-funnel"></i>
</button>

@* Desktop: Show inline filters *@
<div class="d-none d-md-flex gap-2">
    <select class="form-select">...</select>
    <select class="form-select">...</select>
</div>
```

**When to use which:**
- **CSS media queries**: UI layout differences (show/hide elements, change layout).
- **C# platform checks**: Functional differences (use Virtualize vs pagination, enable haptics, etc.).

### Cleanest Approach for Mobile vs Web UX

**Recommendation: Hybrid approach**

1. **Default to mobile-first CSS** (responsive design).
2. **Use C# checks only for functional differences** that can't be handled with CSS.

**Example: Vocabulary page**

```razor
@inject IPlatformService Platform

<div class="vocabulary-container">
    @if (Platform.IsMobile)
    {
        @* Mobile: Virtualized list, no pagination *@
        <Virtualize Items="filteredWords" Context="word" ItemSize="80">
            <VocabularyCard Word="@word" OnTap="() => EditWord(word.Id)" />
        </Virtualize>
    }
    else
    {
        @* Desktop: Paginated table *@
        <table class="table">
            <thead>...</thead>
            <tbody>
                @foreach (var word in currentPageWords)
                {
                    <tr @onclick="() => EditWord(word.Id)">
                        <td>@word.TargetLanguageTerm</td>
                        <td>@word.NativeLanguageTerm</td>
                        <td><span class="badge">@word.Status</span></td>
                    </tr>
                }
            </tbody>
        </table>
        <Pagination CurrentPage="currentPage" TotalPages="totalPages" OnPageChange="ChangePage" />
    }
</div>
```

**Alternative (cleaner):** Use CSS to hide pagination on mobile, same component:

```razor
<div class="vocabulary-list" style="height: 70vh; overflow-y: auto;">
    <Virtualize Items="currentPageWords" Context="word" ItemSize="80">
        <VocabularyCard Word="@word" />
    </Virtualize>
</div>

<div class="pagination-controls d-none d-md-flex">
    <button @onclick="PreviousPage">Previous</button>
    <span>Page @currentPage of @totalPages</span>
    <button @onclick="NextPage">Next</button>
</div>

@code {
    // On mobile: currentPage = 1, currentPageWords = allWords (virtualize handles display)
    // On desktop: currentPage controls which subset of words to show
}
```

---

## Priority Roadmap

### Phase 1: List Virtualization (High Impact, Low Effort)

**Priority:** **Immediate**

1. **Vocabulary.razor**: Replace `@foreach` with `<Virtualize>`.
   - Add scrollable container with fixed height.
   - Pass `filteredWords` to `Items` parameter.
   - Set `ItemSize` estimate (measure card height).
   - Test with 2,000+ words for performance validation.

2. **Resources.razor**: Replace "Show More" pattern with `<Virtualize>`.
   - Remove `displayedResources` computed property.
   - Pass full `resources` list to Virtualize.
   - Simplify UI (no "Show More" button needed).

**Estimated Effort:** 2-4 hours per page.  
**Impact:** Major performance improvement, mobile-native UX.

### Phase 2: Skeleton Loading (Medium Impact, Low Effort)

**Priority:** **High**

1. Create skeleton CSS classes (`.skeleton`, `.skeleton-text`, etc.).
2. Add skeleton placeholders to Vocabulary and Resources pages during `isLoading` state.
3. Replace spinner with skeleton cards (10 placeholder items).

**Estimated Effort:** 1-2 hours.  
**Impact:** Better perceived performance, modern UX.

### Phase 3: Haptic Feedback (Low Impact, Low Effort)

**Priority:** **Medium**

1. Create `IHapticService` wrapper for `HapticFeedback` API.
2. Register service in MAUI and WebApp DI containers.
3. Add haptics to quiz grading (correct/wrong answers).
4. Add haptics to delete confirmation buttons.

**Estimated Effort:** 2-3 hours.  
**Impact:** Subtle but delightful mobile UX enhancement.

### Phase 4: Mobile Touch Patterns (Medium Impact, Medium Effort)

**Priority:** **Low (post-MVP)**

1. Add long-press action sheet to vocabulary cards (edit/delete actions).
2. Use Bootstrap offcanvas for action sheets (already established pattern).
3. Test on iOS and Android for consistent behavior.

**Estimated Effort:** 4-6 hours.  
**Impact:** More mobile-native interaction model.

### Phase 5: Pull-to-Refresh (Low Priority)

**Priority:** **Low (post-MVP)**

1. Only implement if user feedback demands it.
2. If implemented, use CSS + JS interop approach (no native RefreshView).
3. Create reusable component for other pages.

**Estimated Effort:** 8-12 hours (complex).  
**Impact:** Nice-to-have, but refresh button suffices for MVP.

---

## Conclusion

Blazor Hybrid supports mobile-native UX patterns through a combination of built-in components (`<Virtualize>`), CSS responsive design, and targeted JavaScript interop. For SentenceStudio:

1. **Virtualize lists immediately** for performance and mobile UX (highest ROI).
2. **Add skeleton loading** for polish (quick win).
3. **Use haptics** for quiz feedback (delightful detail).
4. **Defer pull-to-refresh** unless user research shows demand.
5. **Leverage Bootstrap + CSS** for most responsive UX (minimize C# platform checks).

This approach balances mobile-native UX with maintainability and code reuse across web and mobile platforms.

---

**Next Steps:**
- Review findings with Captain.
- Create GitHub issues for Phase 1 and 2 implementation.
- Prototype Virtualize on Vocabulary.razor to validate approach.
