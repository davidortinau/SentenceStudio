# Bootstrap → MauiReactor Design Mapping Guide

**Author:** Inara (Design/DevRel)  
**Date:** 2026-02-17  
**Purpose:** Translation guide for porting SentenceStudio's Blazor Hybrid Bootstrap UI to native MauiReactor with MauiBootstrapTheme.

---

## Philosophy

Bootstrap in the browser is a **CSS class-based styling system** that applies visual properties at runtime. MauiBootstrapTheme brings this paradigm to native MAUI by **generating ResourceDictionary styles from Bootstrap CSS** at build time, then applying them via attached properties and fluent extension methods in MauiReactor.

**Key principle:** Where Bootstrap uses `class="btn-primary"`, MauiReactor uses `.Primary()`. Where Bootstrap uses `<div class="d-flex gap-3">`, MauiReactor uses `HStack(spacing: 12, ...)`.

---

## 1. Layout Mapping

### 1.1 Flexbox → HStack/VStack

Bootstrap's flexbox utilities map directly to MauiReactor stack layouts.

| Bootstrap HTML | MauiReactor C# | Notes |
|---------------|----------------|-------|
| `<div class="d-flex">` | `HStack(...)` | Horizontal flex |
| `<div class="d-flex flex-column">` | `VStack(...)` | Vertical flex |
| `<div class="d-flex gap-2">` | `HStack(spacing: 8, ...)` | 8px spacing (Bootstrap level 2) |
| `<div class="d-flex gap-3">` | `HStack(spacing: 16, ...)` | 16px spacing (Bootstrap level 3) |
| `<div class="d-flex justify-content-between">` | `HStack(spacing: 0, item1, new Spacer(), item2)` | Use `Spacer()` for space-between |
| `<div class="d-flex align-items-center">` | `HStack(...).VCenter()` | Vertical centering |
| `<div class="d-flex align-items-start">` | `HStack(...).VStart()` | Align top |
| `<div class="d-flex align-items-end">` | `HStack(...).VEnd()` | Align bottom |

**Example:**

```html
<!-- Bootstrap -->
<div class="d-flex gap-2 align-items-center">
    <i class="bi bi-check-circle"></i>
    <span>Complete</span>
</div>
```

```csharp
// MauiReactor
HStack(spacing: 8,
    Image()
        .Source(BootstrapIcons.Create(BootstrapIcons.CheckCircle, color: Colors.Green, size: 20)),
    Label("Complete")
).VCenter()
```

### 1.2 Bootstrap Spacing Scale

Bootstrap uses a 0-5 spacing scale. MauiBootstrapTheme provides:

| Level | Pixels | Bootstrap class | MauiReactor |
|-------|--------|----------------|-------------|
| 0 | 0px | `m-0`, `p-0` | `.MarginLevel(0)`, `.PaddingLevel(0)` |
| 1 | 4px | `m-1`, `p-1` | `.MarginLevel(1)`, `.PaddingLevel(1)` |
| 2 | 8px | `m-2`, `p-2` | `.MarginLevel(2)`, `.PaddingLevel(2)` |
| 3 | 16px | `m-3`, `p-3` | `.MarginLevel(3)`, `.PaddingLevel(3)` |
| 4 | 24px | `m-4`, `p-4` | `.MarginLevel(4)`, `.PaddingLevel(4)` |
| 5 | 48px | `m-5`, `p-5` | `.MarginLevel(5)`, `.PaddingLevel(5)` |

**Example:**

```html
<!-- Bootstrap -->
<div class="card p-3 mb-4">
    <p class="m-0">Content</p>
</div>
```

```csharp
// MauiReactor
Border(
    Label("Content").MarginLevel(0)
)
.StyleClass("card")
.PaddingLevel(3)
.MarginLevel(4)  // bottom margin
```

### 1.3 Grid → Grid or FlexLayout

Bootstrap's responsive grid system (`row`, `col-*`) requires more adaptation in native MAUI.

| Bootstrap HTML | MauiReactor C# | Notes |
|---------------|----------------|-------|
| `<div class="row g-3">` | `Grid(columns: "*,*,*", spacing: 16, ...)` | Fixed 3-column grid |
| `<div class="col-6">` | Grid column with `ColumnSpan(1)` or use FlexLayout | 50% width |
| `<div class="col-md-4">` | Use `OnIdiom` or runtime device detection | Responsive breakpoint |

**For simple equal-width columns**, use Grid:

```html
<!-- Bootstrap -->
<div class="row g-3">
    <div class="col-6">Item 1</div>
    <div class="col-6">Item 2</div>
</div>
```

```csharp
// MauiReactor
Grid(columns: "*,*", spacing: 16,
    RenderItem1().GridColumn(0),
    RenderItem2().GridColumn(1)
)
```

**For responsive columns** (e.g., 2 on mobile, 3 on tablet, 4 on desktop), use `FlexLayout`:

```csharp
FlexLayout()
    .Wrap(Microsoft.Maui.Layouts.FlexWrap.Wrap)
    .JustifyContent(Microsoft.Maui.Layouts.FlexJustify.Start)
    .Children([
        RenderCard1().FlexGrow(1).FlexBasis(new FlexBasis(45, isRelative: true)), // ~50% on mobile
        RenderCard2().FlexGrow(1).FlexBasis(new FlexBasis(45, isRelative: true)),
        RenderCard3().FlexGrow(1).FlexBasis(new FlexBasis(45, isRelative: true)),
    ])
```

---

## 2. Component Mapping

### 2.1 Buttons

Bootstrap button classes map to MauiReactor fluent methods.

| Bootstrap HTML | MauiReactor C# |
|---------------|----------------|
| `<button class="btn btn-primary">` | `Button("Text").Primary()` |
| `<button class="btn btn-secondary">` | `Button("Text").Secondary()` |
| `<button class="btn btn-success">` | `Button("Text").Success()` |
| `<button class="btn btn-danger">` | `Button("Text").Danger()` |
| `<button class="btn btn-warning">` | `Button("Text").Warning()` |
| `<button class="btn btn-info">` | `Button("Text").Info()` |
| `<button class="btn btn-light">` | `Button("Text").Light()` |
| `<button class="btn btn-dark">` | `Button("Text").Dark()` |
| `<button class="btn btn-outline-primary">` | `Button("Text").Primary().Outlined()` |
| `<button class="btn btn-outline-secondary">` | `Button("Text").Secondary().Outlined()` |
| `<button class="btn btn-sm">` | `Button("Text").Small()` |
| `<button class="btn btn-lg">` | `Button("Text").Large()` |
| `<button class="btn rounded-pill">` | `Button("Text").Pill()` |

**Example:**

```html
<!-- Bootstrap -->
<button class="btn btn-primary" onclick="save()">
    <i class="bi bi-save me-2"></i>Save
</button>
```

```csharp
// MauiReactor
Button("Save")
    .Primary()
    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Save, Colors.White, 20))
    .OnClicked(Save)
```

### 2.2 Button Groups

Bootstrap button groups use `btn-group`. In MauiReactor, use `HStack` with zero spacing:

```html
<!-- Bootstrap -->
<div class="btn-group w-100" role="group">
    <button class="btn btn-primary">Option 1</button>
    <button class="btn btn-outline-secondary">Option 2</button>
</div>
```

```csharp
// MauiReactor
HStack(spacing: 0,
    Button("Option 1").Primary().HFill(),
    Button("Option 2").Secondary().Outlined().HFill()
)
.HFill()
```

### 2.3 Cards

Bootstrap cards use `card` class with optional `p-*` padding. In MauiReactor, use `Border` with `StyleClass("card")`:

```html
<!-- Bootstrap -->
<div class="card card-ss p-3 mb-3">
    <h5 class="ss-title2 mb-2">Card Title</h5>
    <p class="ss-body1 m-0">Card content here.</p>
</div>
```

```csharp
// MauiReactor
Border(
    VStack(spacing: 8,
        Label("Card Title").H5(),
        Label("Card content here.").MarginLevel(0)
    )
)
.StyleClass("card")
.PaddingLevel(3)
.MarginLevel(3)  // bottom margin
```

**Note:** `card-ss` is a custom SentenceStudio class that may add shadows or borders. Check the CSS for specifics.

### 2.4 Badges

Bootstrap badges use `badge bg-*` classes. MauiReactor uses `.Badge()`:

```html
<!-- Bootstrap -->
<span class="badge bg-warning text-dark">
    <i class="bi bi-fire me-1"></i>5 day streak
</span>
```

```csharp
// MauiReactor
Label($"🔥 5 day streak")
    .Badge(BootstrapVariant.Warning)
    .TextColor(Colors.Black)  // text-dark equivalent
```

### 2.5 Progress Bars

Bootstrap progress bars use `progress` and `progress-bar`:

```html
<!-- Bootstrap -->
<div class="progress" style="height: 8px;">
    <div class="progress-bar bg-success" 
         style="width: 75%"
         role="progressbar"></div>
</div>
```

```csharp
// MauiReactor
ProgressBar()
    .Progress(0.75)
    .ProgressColor(BootstrapTheme.Current.Success)
    .BootstrapHeight()  // Sets standard Bootstrap progress height (16px)
    .HeightRequest(8)   // Or override manually
```

### 2.6 Spinners / Activity Indicators

```html
<!-- Bootstrap -->
<div class="spinner-border text-primary" role="status"></div>
```

```csharp
// MauiReactor
ActivityIndicator()
    .IsRunning(true)
    .Color(BootstrapTheme.Current.Primary)
```

### 2.7 Forms

#### Entry (Text Input)

```html
<!-- Bootstrap -->
<input type="text" class="form-control" placeholder="Enter text">
```

```csharp
// MauiReactor
Entry()
    .Placeholder("Enter text")
    .BootstrapHeight()  // Standard Bootstrap input height (38px)
```

#### Picker (Select)

```html
<!-- Bootstrap -->
<select class="form-select">
    <option>Option 1</option>
    <option>Option 2</option>
</select>
```

```csharp
// MauiReactor
Picker()
    .ItemsSource(["Option 1", "Option 2"])
    .BootstrapHeight()
```

---

## 3. Typography Mapping

### 3.1 Custom SentenceStudio Typography

SentenceStudio defines custom typography classes in `app.css`. Map these to MauiReactor headings or manual font sizes:

| Bootstrap CSS Class | MauiReactor | Font Size | Line Height |
|---------------------|-------------|-----------|-------------|
| `.ss-display` | `.Heading(0)` or manual | 60px | 1.17 |
| `.ss-large-title` | `.Heading(0)` or manual | 34px | 1.21 |
| `.ss-title1` | `.H1()` | 28px | 1.21 |
| `.ss-title2` | `.H2()` | 22px | 1.27 |
| `.ss-title3` | `.H3()` | 20px | 1.25 |
| `.ss-headline` | Manual | 32px | Default |
| `.ss-subheadline` | Manual | 24px | Default |
| `.ss-body1` | Default Label | 17px | 1.29 |
| `.ss-body1-strong` | `.FontAttributes(FontAttributes.Bold)` | 17px | 1.29 |
| `.ss-body2` | Manual | 15px | 1.33 |
| `.ss-caption1` | `.Small()` or manual | 12px | 1.33 |

**Example:**

```html
<!-- Bootstrap -->
<h1 class="ss-title1 mb-3">Dashboard</h1>
<p class="ss-body1">Welcome back!</p>
```

```csharp
// MauiReactor
VStack(spacing: 12,
    Label("Dashboard").H1().MarginLevel(3),
    Label("Welcome back!")  // Default is body text
)
```

### 3.2 Bootstrap Native Typography

| Bootstrap Class | MauiReactor |
|-----------------|-------------|
| `h1`, `.h1` | `.H1()` |
| `h2`, `.h2` | `.H2()` |
| `h3`, `.h3` | `.H3()` |
| `h4`, `.h4` | `.H4()` |
| `h5`, `.h5` | `.H5()` |
| `h6`, `.h6` | `.H6()` |
| `.lead` | `.Lead()` |
| `.text-muted` | `.Muted()` |
| `.small` | `.Small()` |
| `<mark>` | `.TextStyle(BootstrapTextStyle.Mark)` |

### 3.3 Text Colors

Bootstrap text color utilities:

| Bootstrap Class | MauiReactor |
|-----------------|-------------|
| `.text-primary` | `.TextColor(BootstrapVariant.Primary)` |
| `.text-secondary` | `.TextColor(BootstrapVariant.Secondary)` |
| `.text-success` | `.TextColor(BootstrapVariant.Success)` |
| `.text-danger` | `.TextColor(BootstrapVariant.Danger)` |
| `.text-warning` | `.TextColor(BootstrapVariant.Warning)` |
| `.text-info` | `.TextColor(BootstrapVariant.Info)` |
| `.text-light` | `.TextColor(BootstrapVariant.Light)` |
| `.text-dark` | `.TextColor(BootstrapVariant.Dark)` |
| `.text-muted` | `.Muted()` |

**Custom SentenceStudio text colors:**

```html
<!-- Bootstrap -->
<span class="text-secondary-ss">Muted text</span>
```

```csharp
// MauiReactor
Label("Muted text")
    .TextColor(BootstrapTheme.Current.SecondaryColor)  // Use DynamicResource
```

### 3.4 Text Alignment & Truncation

| Bootstrap Class | MauiReactor |
|-----------------|-------------|
| `.text-start` | `.HorizontalTextAlignment(TextAlignment.Start)` |
| `.text-center` | `.HorizontalTextAlignment(TextAlignment.Center)` |
| `.text-end` | `.HorizontalTextAlignment(TextAlignment.End)` |
| `.text-truncate` | `.LineBreakMode(LineBreakMode.TailTruncation).MaxLines(1)` |
| `.fw-bold` | `.FontAttributes(FontAttributes.Bold)` |
| `.fw-semibold` | `.FontAttributes(FontAttributes.Bold)` (native has no semibold) |
| `.fw-normal` | Default |
| `.fst-italic` | `.FontAttributes(FontAttributes.Italic)` |

---

## 4. Icon Mapping

Bootstrap Icons in HTML use `<i class="bi bi-icon-name">`. In MauiReactor, use `IconFont.Maui.BootstrapIcons`.

### 4.1 Bootstrap Icons API

The library provides a static `BootstrapIcons.Create()` method:

```csharp
public static FontImageSource Create(string glyph, Color? color = null, double size = 24d)
```

**Glyph constants** are source-generated from the Bootstrap Icons font. Examples:

- `BootstrapIcons.HouseDoor`
- `BootstrapIcons.Book`
- `BootstrapIcons.CardText`
- `BootstrapIcons.CheckCircle`
- `BootstrapIcons.Fire`
- `BootstrapIcons.Gear`
- `BootstrapIcons.Person`
- `BootstrapIcons.Search`
- `BootstrapIcons.Save`
- `BootstrapIcons.Trash`

**Note:** The glyph constant names use **PascalCase** (e.g., `HouseDoor`), while Bootstrap CSS uses **kebab-case** (e.g., `house-door`).

### 4.2 Usage Patterns

#### As ImageSource in ImageButton

```html
<!-- Bootstrap -->
<button class="btn btn-icon">
    <i class="bi bi-trash"></i>
</button>
```

```csharp
// MauiReactor
ImageButton()
    .Source(BootstrapIcons.Create(BootstrapIcons.Trash, Colors.Red, 20))
    .OnClicked(Delete)
```

#### As ImageSource in Button

```csharp
Button("Delete")
    .Danger()
    .ImageSource(BootstrapIcons.Create(BootstrapIcons.Trash, Colors.White, 20))
    .OnClicked(Delete)
```

#### As Image in layouts

```csharp
HStack(spacing: 8,
    Image()
        .Source(BootstrapIcons.Create(BootstrapIcons.Fire, Colors.Orange, 24)),
    Label($"{streakCount} day streak")
).VCenter()
```

#### Inline with Label (emoji pattern)

For simple icons that don't need color changes, use **emoji** instead of image sources:

```csharp
Label($"🔥 {streakCount} day streak")  // Simpler than an HStack + Image
```

### 4.3 Common Icons in SentenceStudio

| Bootstrap CSS | Icon Constant | Usage |
|---------------|--------------|-------|
| `bi-house-door` | `BootstrapIcons.HouseDoor` | Dashboard/Home |
| `bi-book` | `BootstrapIcons.Book` | Learning Resources |
| `bi-card-text` | `BootstrapIcons.CardText` | Vocabulary |
| `bi-soundwave` | `BootstrapIcons.Soundwave` | Minimal Pairs |
| `bi-bullseye` | `BootstrapIcons.Bullseye` | Skills |
| `bi-box-arrow-in-down` | `BootstrapIcons.BoxArrowInDown` | Import |
| `bi-person` | `BootstrapIcons.Person` | Profile |
| `bi-gear` | `BootstrapIcons.Gear` | Settings |
| `bi-calendar-check` | `BootstrapIcons.CalendarCheck` | Today's Plan |
| `bi-sliders` | `BootstrapIcons.Sliders` | Choose My Own |
| `bi-fire` | `BootstrapIcons.Fire` | Streak |
| `bi-arrow-clockwise` | `BootstrapIcons.ArrowClockwise` | Regenerate |
| `bi-check-circle` | `BootstrapIcons.CheckCircle` | Complete/Success |
| `bi-x-circle` | `BootstrapIcons.XCircle` | Cancel/Error |
| `bi-play-circle` | `BootstrapIcons.PlayCircle` | Play/Start |
| `bi-chevron-right` | `BootstrapIcons.ChevronRight` | Next/Forward |
| `bi-chevron-left` | `BootstrapIcons.ChevronLeft` | Back |

---

## 5. Color & Theme Mapping

### 5.1 Bootstrap Color Variables

Bootstrap defines color variables in CSS. MauiBootstrapTheme exposes these as `DynamicResource` keys and typed properties on `BootstrapTheme.Current`:

| Bootstrap CSS Variable | MauiReactor Access |
|------------------------|-------------------|
| `var(--bs-primary)` | `BootstrapTheme.Current.Primary` |
| `var(--bs-secondary)` | `BootstrapTheme.Current.Secondary` |
| `var(--bs-success)` | `BootstrapTheme.Current.Success` |
| `var(--bs-danger)` | `BootstrapTheme.Current.Danger` |
| `var(--bs-warning)` | `BootstrapTheme.Current.Warning` |
| `var(--bs-info)` | `BootstrapTheme.Current.Info` |
| `var(--bs-light)` | `BootstrapTheme.Current.Light` |
| `var(--bs-dark)` | `BootstrapTheme.Current.Dark` |
| `var(--bs-body-bg)` | `BootstrapTheme.Current.BodyBg` |
| `var(--bs-body-color)` | `BootstrapTheme.Current.BodyColor` |
| `var(--bs-secondary-bg)` | `BootstrapTheme.Current.SecondaryBg` |
| `var(--bs-tertiary-bg)` | `BootstrapTheme.Current.TertiaryBg` |
| `var(--bs-border-color)` | `BootstrapTheme.Current.BorderColor` |

**Example:**

```html
<!-- Bootstrap -->
<div style="background-color: var(--bs-primary); color: var(--ss-on-primary);">
    Primary background
</div>
```

```csharp
// MauiReactor
Border(
    Label("Primary background")
        .TextColor(BootstrapTheme.Current.OnPrimary)
)
.Background(BootstrapTheme.Current.Primary)
```

### 5.2 Background Variants

| Bootstrap Class | MauiReactor |
|-----------------|-------------|
| `.bg-primary` | `.Background(BootstrapVariant.Primary)` |
| `.bg-secondary` | `.Background(BootstrapVariant.Secondary)` |
| `.bg-success` | `.Background(BootstrapVariant.Success)` |
| `.bg-danger` | `.Background(BootstrapVariant.Danger)` |
| `.bg-warning` | `.Background(BootstrapVariant.Warning)` |
| `.bg-info` | `.Background(BootstrapVariant.Info)` |
| `.bg-light` | `.Background(BootstrapVariant.Light)` |
| `.bg-dark` | `.Background(BootstrapVariant.Dark)` |

### 5.3 Opacity

```html
<!-- Bootstrap -->
<div class="opacity-75">Content</div>
```

```csharp
// MauiReactor
Border(...)
    .Opacity(0.75)
```

---

## 6. Responsive Patterns

### 6.1 Display Utilities (Show/Hide)

Bootstrap uses `d-none`, `d-md-block`, etc. for responsive visibility. In MauiReactor, use **conditional rendering** or `OnIdiom`:

```html
<!-- Bootstrap: Hide on mobile, show on desktop -->
<div class="d-none d-md-block">
    <h1>Welcome Message (Desktop Only)</h1>
</div>
```

```csharp
// MauiReactor: Conditional rendering
var isDesktop = DeviceInfo.Current.Idiom == DeviceIdiom.Desktop;

if (isDesktop)
{
    Label("Welcome Message (Desktop Only)").H1();
}
```

**Or use `OnIdiom`:**

```csharp
Label("Welcome")
    .IsVisible(new OnIdiom<bool> { Phone = false, Tablet = true, Desktop = true })
```

### 6.2 Responsive Columns

Bootstrap's `col-6 col-md-4 col-lg-3` pattern (50% on mobile, 33% on tablet, 25% on desktop) requires runtime detection in native MAUI.

**Option 1: FlexLayout with adaptive basis**

```csharp
FlexLayout()
    .Wrap(FlexWrap.Wrap)
    .Children([
        RenderCard().FlexBasis(new FlexBasis(
            DeviceInfo.Current.Idiom == DeviceIdiom.Phone ? 50f : 
            DeviceInfo.Current.Idiom == DeviceIdiom.Tablet ? 33f : 25f,
            isRelative: true
        ))
    ])
```

**Option 2: Grid with adaptive column definitions**

```csharp
var columns = DeviceInfo.Current.Idiom switch
{
    DeviceIdiom.Phone => "*,*",      // 2 columns
    DeviceIdiom.Tablet => "*,*,*",   // 3 columns
    _ => "*,*,*,*"                   // 4 columns
};

Grid(columns: columns, spacing: 16,
    RenderCard1().GridColumn(0),
    RenderCard2().GridColumn(1),
    RenderCard3().GridColumn(2),
    RenderCard4().GridColumn(3)
)
```

### 6.3 Detecting Phone vs Desktop

```csharp
var isPhone = DeviceInfo.Current.Idiom == DeviceIdiom.Phone;
var isTablet = DeviceInfo.Current.Idiom == DeviceIdiom.Tablet;
var isDesktop = DeviceInfo.Current.Idiom == DeviceIdiom.Desktop;
```

---

## 7. Page Structure Pattern

### 7.1 Standard Page Skeleton

Bootstrap Blazor pages follow this structure:

```html
<!-- Blazor -->
<PageHeader Title="Page Title" />
<div class="mb-3">
    <h1 class="ss-title1">Page Heading</h1>
</div>
<div class="card p-3">
    Content here
</div>
```

MauiReactor equivalent:

```csharp
public override VisualNode Render()
{
    return ContentPage("Page Title",
        Grid(rows: "Auto,*", columns: "*",
            RenderHeader().GridRow(0),
            ScrollView(
                VStack(spacing: 16,
                    Label("Page Heading").H1().MarginLevel(3),
                    Border(
                        Label("Content here")
                    )
                    .StyleClass("card")
                    .PaddingLevel(3)
                )
                .Padding(16)
            )
            .GridRow(1)
        )
    );
}
```

### 7.2 Shell TitleView for Custom Headers

For custom navigation content (like activity timers), use `Shell.TitleView`:

```csharp
ContentPage(
    Grid(...)
)
.Set(MauiControls.Shell.TitleViewProperty, RenderCustomTitleView())
```

**DO NOT use `NavigationPage.TitleView`** — SentenceStudio uses Shell navigation.

### 7.3 Scrollable Content

Always wrap page content in `ScrollView` to prevent layout issues:

```csharp
ContentPage("Title",
    ScrollView(
        VStack(spacing: 16,
            RenderSection1(),
            RenderSection2(),
            RenderSection3()
        )
        .Padding(16)
    )
)
```

**NEVER put vertically scrolling controls (like `CollectionView`) inside `VStack` without constraints** — use Grid with star-sized rows instead:

```csharp
Grid(rows: "Auto,Auto,*", columns: "*",
    RenderHeader().GridRow(0),
    RenderFilters().GridRow(1),
    CollectionView(...).GridRow(2)  // Constrained by star row
)
```

---

## 8. Navigation Mapping

### 8.1 Blazor Sidebar → Shell Flyout

Blazor's `MainLayout.razor` has a sidebar navigation. In MauiReactor, this becomes `Shell` with `FlyoutItem`:

**Blazor NavMenu:**

```html
<nav class="nav flex-column p-2 gap-1">
    <a class="nav-link" href="dashboard">
        <i class="bi bi-house-door"></i> Dashboard
    </a>
    <a class="nav-link" href="resources">
        <i class="bi bi-book"></i> Learning Resources
    </a>
</nav>
```

**MauiReactor AppShell:**

```csharp
Shell(
    FlyoutItem("Dashboard",
        ShellContent()
            .Title("Dashboard")
            .Icon(BootstrapIcons.Create(BootstrapIcons.HouseDoor, Colors.Gray, 24))
            .RenderContent(() => new DashboardPage())
            .Route("dashboard")
    ),
    FlyoutItem("Learning Resources",
        ShellContent()
            .Title("Learning Resources")
            .Icon(BootstrapIcons.Create(BootstrapIcons.Book, Colors.Gray, 24))
            .RenderContent(() => new ListLearningResourcesPage())
            .Route("resources")
    )
)
```

### 8.2 Navigation between Pages

Always use **`Shell.GoToAsync()`**, never `Navigation.PushAsync()`:

```csharp
// Navigate forward
await MauiControls.Shell.Current.GoToAsync(nameof(DetailPage));

// Navigate back
await MauiControls.Shell.Current.GoToAsync("..");

// Navigate with parameters
await MauiControls.Shell.Current.GoToAsync<DetailPageProps>(
    nameof(DetailPage), 
    props => props.ItemId = itemId
);
```

---

## 9. Real-World Examples

### 9.1 Dashboard Mode Switcher

**Blazor:**

```html
<div class="btn-group w-100 mb-4" role="group">
    <button class="btn @(isTodaysPlanMode ? "btn-ss-primary" : "btn-outline-secondary")"
            @onclick="() => SetMode(true)">
        <i class="bi bi-calendar-check me-1"></i>Today's Plan
    </button>
    <button class="btn @(!isTodaysPlanMode ? "btn-ss-primary" : "btn-outline-secondary")"
            @onclick="() => SetMode(false)">
        <i class="bi bi-sliders me-1"></i>Choose My Own
    </button>
</div>
```

**MauiReactor:**

```csharp
HStack(spacing: 0,
    Button($"📅 Today's Plan")
        .Primary(state.Value.IsTodaysPlanMode)
        .Secondary(!state.Value.IsTodaysPlanMode)
        .Outlined(!state.Value.IsTodaysPlanMode)
        .HFill()
        .OnClicked(() => SetMode(true)),
    Button($"🎛️ Choose My Own")
        .Primary(!state.Value.IsTodaysPlanMode)
        .Secondary(state.Value.IsTodaysPlanMode)
        .Outlined(state.Value.IsTodaysPlanMode)
        .HFill()
        .OnClicked(() => SetMode(false))
)
.HFill()
.MarginLevel(4)  // mb-4
```

**Or using BootstrapIcons:**

```csharp
HStack(spacing: 0,
    Button("Today's Plan")
        .ImageSource(BootstrapIcons.Create(
            BootstrapIcons.CalendarCheck, 
            Colors.White, 
            20
        ))
        .Primary(state.Value.IsTodaysPlanMode)
        .Secondary(!state.Value.IsTodaysPlanMode)
        .Outlined(!state.Value.IsTodaysPlanMode)
        .HFill()
        .OnClicked(() => SetMode(true)),
    Button("Choose My Own")
        .ImageSource(BootstrapIcons.Create(
            BootstrapIcons.Sliders, 
            Colors.White, 
            20
        ))
        .Primary(!state.Value.IsTodaysPlanMode)
        .Secondary(state.Value.IsTodaysPlanMode)
        .Outlined(state.Value.IsTodaysPlanMode)
        .HFill()
        .OnClicked(() => SetMode(false))
)
.HFill()
.MarginLevel(4)
```

### 9.2 Streak Badge

**Blazor:**

```html
<div class="d-flex align-items-center gap-2 mb-3">
    <span class="badge bg-warning text-dark fs-6">
        <i class="bi bi-fire me-1"></i>@streak.CurrentStreak day streak
    </span>
    @if (streak.LongestStreak > streak.CurrentStreak)
    {
        <span class="text-secondary-ss small">Best: @streak.LongestStreak days</span>
    }
</div>
```

**MauiReactor:**

```csharp
HStack(spacing: 8,
    Label($"🔥 {state.Value.CurrentStreak} day streak")
        .Badge(BootstrapVariant.Warning)
        .TextColor(Colors.Black)
        .FontSize(16),
    state.Value.LongestStreak > state.Value.CurrentStreak
        ? Label($"Best: {state.Value.LongestStreak} days")
            .Muted()
            .Small()
        : null
)
.VCenter()
.MarginLevel(3)
```

### 9.3 Progress Card

**Blazor:**

```html
<div class="card card-ss p-3 mb-3">
    <div class="d-flex justify-content-between align-items-center mb-2">
        <span class="ss-body1 fw-semibold">Today's Progress</span>
        <span class="text-secondary-ss small">@CompletedCount / @TotalCount</span>
    </div>
    <div class="progress" style="height: 8px;">
        <div class="progress-bar bg-success" 
             style="width: @(CompletionPercentage)%"></div>
    </div>
</div>
```

**MauiReactor:**

```csharp
Border(
    VStack(spacing: 8,
        HStack(spacing: 0,
            Label("Today's Progress")
                .FontAttributes(FontAttributes.Bold)
                .HFill(),
            Label($"{state.Value.CompletedCount} / {state.Value.TotalCount}")
                .Muted()
                .Small()
        ),
        ProgressBar()
            .Progress(state.Value.CompletionPercentage / 100.0)
            .ProgressColor(BootstrapTheme.Current.Success)
            .HeightRequest(8)
    )
)
.StyleClass("card")
.PaddingLevel(3)
.MarginLevel(3)
```

---

## 10. Key Differences & Gotchas

### 10.1 StyleClass vs Fluent Methods

MauiBootstrapTheme supports **both** approaches:

```csharp
// Approach 1: Fluent methods (recommended for variants)
Button("Click Me").Primary()

// Approach 2: StyleClass (for complex styles)
Button("Click Me").StyleClass("btn-primary")
```

**Recommendation:** Use fluent methods (`.Primary()`, `.Secondary()`) for simple variant changes, and `StyleClass()` for custom or complex styles defined in CSS.

### 10.2 Bootstrap Icon Names

Bootstrap CSS uses **kebab-case** (`house-door`), but the C# constants use **PascalCase** (`HouseDoor`). To convert:

1. Split on hyphens: `house-door` → `["house", "door"]`
2. Capitalize each part: `["House", "Door"]`
3. Join: `HouseDoor`

**Quick reference:**

| Bootstrap CSS | C# Constant |
|---------------|------------|
| `bi-house-door` | `BootstrapIcons.HouseDoor` |
| `bi-calendar-check` | `BootstrapIcons.CalendarCheck` |
| `bi-arrow-clockwise` | `BootstrapIcons.ArrowClockwise` |
| `bi-x-circle` | `BootstrapIcons.XCircle` |
| `bi-check-circle` | `BootstrapIcons.CheckCircle` |

### 10.3 Spacing Scale Gotcha

Bootstrap CSS classes use **abbreviated names** (`m-3`, `p-4`), but MauiReactor uses **explicit method names**:

| Bootstrap | MauiReactor |
|-----------|-------------|
| `mb-3` | `.MarginLevel(3)` (applies to all sides — use `.Margin(new Thickness(0,0,0,16))` for bottom only) |
| `me-2` | Manual: `.Margin(new Thickness(0,8,0,0))` (8px = level 2) |
| `p-3` | `.PaddingLevel(3)` |

**For directional spacing**, use manual `Thickness`:

```csharp
Label("Text")
    .Margin(new Thickness(left: 0, top: 0, right: 8, bottom: 0))  // me-2
```

### 10.4 No Auto-Layout in Native

Bootstrap's flexbox auto-sizes elements based on content. In native MAUI, you must be more explicit:

```html
<!-- Bootstrap: auto-sizes -->
<div class="d-flex gap-2">
    <button class="btn btn-primary">Short</button>
    <button class="btn btn-secondary">Very Long Text Here</button>
</div>
```

In MauiReactor, buttons will size to their content by default, but you may need to use `.HFill()` or `.WidthRequest()` for specific layouts.

### 10.5 Font Scale

Bootstrap CSS uses `calc(17px * var(--ss-font-scale, 1))` to scale fonts. In native MAUI, font scaling is handled by the OS (iOS Dynamic Type, Android Font Scale). Don't replicate CSS font scaling — respect OS settings instead.

---

## 11. Migration Checklist

When porting a Blazor page to MauiReactor, follow this checklist:

- [ ] **Layout**: Replace `d-flex`, `row`, `col-*` with `HStack`, `VStack`, `Grid`
- [ ] **Buttons**: Replace `btn btn-primary` with `Button().Primary()`
- [ ] **Cards**: Replace `card p-3` with `Border().StyleClass("card").PaddingLevel(3)`
- [ ] **Icons**: Replace `<i class="bi bi-*">` with `BootstrapIcons.Create(BootstrapIcons.*, color, size)`
- [ ] **Typography**: Replace `ss-title1`, `h1` with `.H1()` or manual font sizes
- [ ] **Colors**: Replace `bg-success`, `text-danger` with `.Background(BootstrapVariant.Success)`, `.TextColor(BootstrapVariant.Danger)`
- [ ] **Spacing**: Replace `mb-3`, `p-2` with `.MarginLevel(3)`, `.PaddingLevel(2)`
- [ ] **Responsive**: Replace `d-none d-md-block` with conditional rendering or `OnIdiom`
- [ ] **Navigation**: Ensure all navigation uses `Shell.GoToAsync()`, not `Navigation.PushAsync()`
- [ ] **Scrolling**: Wrap content in `ScrollView`, use Grid for `CollectionView`

---

## 12. Resources

- **MauiBootstrapTheme Repo:** [github.com/davidortinau/MauiBootstrapTheme](https://github.com/davidortinau/MauiBootstrapTheme)
- **IconFont.Maui.BootstrapIcons Repo:** [github.com/davidortinau/IconFont.Maui.BootstrapIcons](https://github.com/davidortinau/IconFont.Maui.BootstrapIcons)
- **Bootstrap 5.3 Docs:** [getbootstrap.com/docs/5.3](https://getbootstrap.com/docs/5.3/)
- **Bootstrap Icons:** [icons.getbootstrap.com](https://icons.getbootstrap.com/)
- **MauiReactor Docs:** [adospace.github.io/reactorui-maui](https://adospace.github.io/reactorui-maui/)

---

**End of Guide**
