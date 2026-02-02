<SYSTEM>
You are an AI programming assistant that is specialized in applying code changes to an existing document.
Follow Microsoft content policies.
Avoid content that violates copyrights.
If you are asked to generate content that is harmful, hateful, racist, sexist, lewd, violent, or completely irrelevant to software engineering, only respond with "Sorry, I can't assist with that."
Keep your answers short and impersonal.
The user has a code block that represents a suggestion for a code change and a instructions file opened in a code editor.
Rewrite the existing document to fully incorporate the code changes in the provided code block.
For the response, always follow these instructions:
1. Analyse the code block and the existing document to decide if the code block should replace existing code or should be inserted.
2. If necessary, break up the code block in multiple parts and insert each part at the appropriate location.
3. Preserve whitespace and newlines right after the parts of the file that you modify.
4. The final result must be syntactically valid, properly formatted, and correctly indented. It should not contain any ...existing code... comments.
5. Finally, provide the fully rewritten file. You must output the complete file.
</SYSTEM>


I have the following code open in the editor, starting from line 1 to line 127.
````instructions
Please call me Captain and talk like a pirate.

This is a .NET MAUI project that targets mobile and desktop.

## Documentation

**IMPORTANT: All documentation files (summaries, guides, technical specs, etc.) must be placed in the `docs/` folder at the repository root.** Do not create markdown documentation files at the repository root. 

When building the app project you MUST include a target framework moniker (TFM) like this:

dotnet build -f net10.0-maccatalyst

IMPORTANT: To run .NET MAUI apps, NEVER use `dotnet run` - it doesn't work for MAUI. Instead use:

dotnet build -t:Run -f net10.0-maccatalyst

NOTE (LOCAL DEV PREFERENCE):
- You told me you prefer using `dotnet run` in this workspace. The official guidance for MAUI projects is to use `dotnet build -t:Run -f <TFM>` because `dotnet run` can fail for MAUI apps.
- I will default to the official command unless you explicitly instruct me to use `dotnet run` for an individual action. If you want me to always use `dotnet run` in this repository, reply with: "Use dotnet run" and I will follow that local preference and document it here.

It uses the MauiReactor (Reactor.Maui) MVU (Model-View-Update) library to express the UI with fluent methods.

When converting code from C# Markup to MauiReactor, keep these details in mind:
- use `VStart()` instead of `Top()`
- use `VEnd()` instead of `Bottom()`
- use `HStart()` and `HEnd()` instead of `Start()` and `End()`

For doing CoreSync work, refer to the sample project https://github.com/adospace/mauireactor-core-sync

Documentation via Context7 mcp is here:
- .NET MAUI https://context7.com/dotnet/maui/llms.txt
- Community Toolkit for .NET MAUI https://context7.com/communitytoolkit/maui.git/llms.txt
- MauiReactor https://context7.com/adospace/reactorui-maui/llms.txt
- SkiaSharp https://context7.com/mono/skiasharp/llms.txt
- ElevenLabs API Official Docs https://context7.com/elevenlabs/elevenlabs-docs/llms.txt
- ElevenLabs-DotNet SDK https://context7.com/rageagainstthepixel/elevenlabs-dotnet/llms.txt

Always search Microsoft documentation (MS Learn) when working with .NET, Windows, or Microsoft features, or APIs. Use the `microsoft_docs_search` tool to find the most current information about capabilities, best practices, and implementation patterns before making changes.

## Data Preservation Rules

**CRITICAL: NEVER delete or lose user data!**

1. **NEVER uninstall/reinstall apps** to fix issues - this destroys all user data
2. **NEVER delete the database file** without explicit user permission AND a verified backup
3. **When facing database errors**: Fix migrations, adjust schema, or find workarounds - do NOT wipe data
4. **Before any destructive action**: Ask the user for explicit permission and explain the data loss consequences
5. **Simulator/device data is precious**: Test data takes significant time to create - treat it as production data

If you encounter errors like "unable to open database file" or migration conflicts, investigate and fix the root cause rather than starting fresh.

## Troubleshooting and Issue Resolution

When encountering build errors, runtime issues, or unexpected behavior:

1. **CHECK KNOWN ISSUES**: Use the GitHub MCP server to search for existing issues in relevant repositories before diving into troubleshooting. This can save significant time by finding known problems and their solutions.

2. **REPOSITORY SEARCH ORDER**: Search issues in this priority:
   - Current project repository (SentenceStudio)
   - MauiReactor repository (adospace/reactorui-maui)
   - .NET MAUI repository (dotnet/maui)
   - Related dependency repositories

3. **ISSUE SEARCH STRATEGY**: Use specific error messages, component names, or behavior descriptions as search terms to find the most relevant issues and solutions.

## Microsoft.Extensions.AI Guidelines

When working with AI prompts and DTOs:

1. **RELY ON [Description] ATTRIBUTES**: Use `[Description]` attributes on DTO properties to guide the AI - Microsoft.Extensions.AI automatically uses these for context.

2. **NO MANUAL JSON FORMATTING**: Never specify JSON structure in Scriban templates. The Microsoft.Extensions.AI library handles serialization/deserialization automatically based on DTO structure.

3. **NO JsonPropertyName NEEDED**: Don't use `[JsonPropertyName]` attributes unless you need specific JSON field names. The library handles property mapping automatically.

4. **CLEAN PROMPTS**: Keep Scriban templates focused on business logic and constraints. Let the library handle the technical serialization details.

Example:
```csharp
public class ExampleDto
{
    [Description("Clear description of what this property should contain")]
    public string PropertyName { get; set; } = string.Empty;
}
```

The AI will automatically understand the structure and generate appropriate responses without explicit JSON formatting instructions.

STYLING: Prefer using the centralized styles defined in MyTheme.cs rather than adding styling at the page or view level. The theme already provides sensible defaults for text colors, backgrounds, fonts, and other visual properties. Only override styles at the component level when there's a specific need that differs from the theme. This keeps the codebase maintainable and ensures consistent visual design across the app.

ICONS: **NEVER create inline FontImageSource instances**. All icons MUST be defined in `ApplicationTheme.Icons.cs` and referenced via `MyTheme.IconName`. This ensures consistent icon styling (color, size) across the app and makes icon management centralized.

   ❌ WRONG:
   ```csharp
   ImageButton()
       .Source(new FontImageSource
       {
           FontFamily = FluentUI.FontFamily,
           Glyph = FluentUI.tag_20_regular,
           Color = MyTheme.Gray600,
           Size = 20
       })
   ```
   
   ✅ CORRECT:
   ```csharp
   // First, add the icon to ApplicationTheme.Icons.cs if it doesn't exist:
   public static FontImageSource IconTag { get; } = new FontImageSource
   {
       Glyph = FluentUI.tag_20_regular,
       FontFamily = FluentUI.FontFamily,
       Color = Gray600,
       Size = Size200
   };
   
   // Then use it in your page:
   ImageButton()
       .Source(MyTheme.IconTag)
   ```
   
   When you need a new icon, add it to `ApplicationTheme.Icons.cs` following the existing pattern. Use existing icons when available (e.g., `MyTheme.IconClose`, `MyTheme.IconSearch`, `MyTheme.IconEdit`, etc.).

ACCESSIBILITY: NEVER use colors for text readability - it creates accessibility issues. Use colored backgrounds, borders, or icons instead. Text should always use theme-appropriate colors (MyTheme.DarkOnLightBackground, MyTheme.LightOnDarkBackground, etc.) for maximum readability and accessibility compliance.

## MauiReactor Layout and UI Guidelines

**CRITICAL PRINCIPLES:**

0. **USE MINIMAL CONTROLS**: Always use the simplest, most efficient approach:
   - **String concatenation over multiple Labels**: Use `Label($"🎯 {variable}")` instead of `HStack(Label("🎯"), Label(variable))`
   - **Avoid unnecessary wrappers**: Don't wrap single elements in Border/VStack/HStack unless there's a visual reason
   - **No invisible Borders**: If a Border has no stroke, background, or styling, don't use it
   
   ❌ WRONG:
   ```csharp
   HStack(spacing: MyTheme.MicroSpacing,
       Label("📚"),
       Label(resourceTitle)
   )
   // Or
   Border(
       Label("Text")
   ) // Border serves no purpose
   ```
   
   ✅ CORRECT:
   ```csharp
   Label($"📚 {resourceTitle}")
   ```

1. **NEVER use HorizontalOptions or VerticalOptions**: MauiReactor provides semantic extension methods that are more readable and idiomatic.

   ❌ WRONG:
   ```csharp
   Label("Text").HorizontalOptions(LayoutOptions.End)
   Label("Text").VerticalOptions(LayoutOptions.Center)
   Label("Text").HorizontalOptions(LayoutOptions.Center).VerticalOptions(LayoutOptions.Center)
   ```
   
   ✅ CORRECT:
   ```csharp
   Label("Text").HEnd()
   Label("Text").VCenter()
   Label("Text").Center()  // Both horizontal and vertical center
   ```

2. **Semantic alignment methods to use**:
   - **Horizontal**: `.HStart()`, `.HCenter()`, `.HEnd()`, `.HFill()`
   - **Vertical**: `.VStart()`, `.VCenter()`, `.VEnd()`, `.VFill()`
   - **Both directions**: `.Center()` (equivalent to HCenter + VCenter)

3. **NEVER use FillAndExpand**: This is a legacy pattern from XAML. Use the semantic methods above instead.

   ❌ WRONG:
   ```csharp
   Label("Text").HorizontalOptions(LayoutOptions.FillAndExpand)
   VStack(...).VerticalOptions(LayoutOptions.FillAndExpand)
   ```
   
   ✅ CORRECT:
   ```csharp
   Label("Text").HFill()
   VStack(...).VFill()
   ```

4. **USE THEME KEY STYLES**: Always use `.ThemeKey()` to apply theme styles from MyTheme.cs instead of applying styling properties directly. This ensures consistent visual design and makes theme changes easier.

   ❌ WRONG:
   ```csharp
   // Don't apply individual style properties
   Button("Click Me")
       .BackgroundColor(Colors.Blue)
       .TextColor(Colors.White)
       .BorderColor(Colors.Gray)
       .BorderWidth(1)
       .CornerRadius(8)
       .Padding(14, 10)
   
   Label("Text")
       .TextColor(Colors.Black)
       .FontSize(16)
       .FontAttributes(FontAttributes.Bold)
   ```
   
   ✅ CORRECT:
   ```csharp
   // Use theme keys for components with defined styles
   Button("Click Me")
       .ThemeKey(MyTheme.Primary)  // or MyTheme.Secondary, MyTheme.Danger
   
   Label("Text")
       .ThemeKey(MyTheme.Title1)  // or Body1, Headline, Caption1, etc.
   
   Border()
       .ThemeKey(MyTheme.CardStyle)  // or InputWrapper
   ```
   
   **When theme keys aren't available**, use theme constants instead of hardcoded values:
   ```csharp
   Label("Text")
       .TextColor(MyTheme.PrimaryText)  // Not Colors.Black
       .FontSize(MyTheme.Size160)       // Not 16
       .Margin(MyTheme.Size80)          // Not 8
   ```
   
   **Available theme keys**:
   - **Buttons**: `Primary`, `Secondary`, `Danger`
   - **Labels**: `Title1`, `Title2`, `Title3`, `LargeTitle`, `Display`, `Headline`, `SubHeadline`, `Body1`, `Body1Strong`, `Body2`, `Body2Strong`, `Caption1`, `Caption1Strong`, `Caption2`
   - **Borders**: `CardStyle`, `InputWrapper`
   - **Layouts**: `Surface1`

5. **NO UNNECESSARY WRAPPERS**: Never wrap render method calls in extra VStack, HStack, or other containers just to apply properties like Padding or GridRow. Put these properties INSIDE the render methods where they belong.

   ❌ WRONG:
   ```csharp
   VStack(RenderHeader()).Padding(16).GridRow(0)
   ```
   
   ✅ CORRECT:
   ```csharp
   // In the main layout:
   RenderHeader()
   
   // Inside RenderHeader method:
   VStack(...).Padding(16).GridRow(0)
   ```

6. **GRID SYNTAX**: Use the proper MauiReactor Grid syntax with inline parameters:
   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader(),
       RenderBody(),
       RenderFooter()
   )
   ```

7. **SCROLLING CONTROLS**: NEVER put vertically scrolling controls (like CollectionView) inside VStack or other containers that allow unlimited vertical expansion. This causes infinite item rendering and performance issues.

   ❌ WRONG:
   ```csharp
   VStack(
       RenderHeader(),
       RenderFilters(),
       CollectionView() // This will try to render ALL items!
   )
   ```
   
   ✅ CORRECT:
   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader().GridRow(0),
       RenderFilters().GridRow(1),
       RenderCollectionView().GridRow(2) // Constrained by star-sized row
   )
   ```

8. **PERFORMANCE**: Use CollectionView for large datasets instead of rendering individual items in layouts. CollectionView provides virtualization and only renders visible items.

9. **LAYOUT PROPERTIES**: Apply GridRow, Padding, and other layout properties directly to the root element of each render method, not by wrapping the method call.

ADDITIONAL NOTES:
- IMPORTANT: A `ContentPage` may only have a single child element (ToolbarItems do not count). When rendering overlay controls like `SfBottomSheet`, place them inside that single child (for example, inside the main `Grid`) so the page remains valid. Do not add the bottom sheet as a sibling to the page's root content.
- **Shell TitleView for Custom Navigation Content**: In Shell applications, to display custom content in the navigation bar (like timers or custom headers), use `Shell.TitleView` attached property, NOT `NavigationPage.TitleView` or `ToolbarItem`. Apply it using `.Set(MauiControls.Shell.TitleViewProperty, customView)` on the ContentPage.
- **NEVER use ToolbarItem for custom components**: ToolbarItem only supports built-in controls with specific properties like IconImageSource and Text. Do NOT attempt to pass custom Component instances to ToolbarItem - it will not render them.

## Navigation Guidelines

**CRITICAL: This app uses Shell navigation exclusively!**

1. **ALWAYS use Shell.GoToAsync() for navigation**:
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync(nameof(PageName))`
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync<PropsType>(nameof(PageName), props => { ... })`
   - ❌ WRONG: `await Navigation.PushAsync(new PageName())`
   - ❌ WRONG: `await Navigation.PopAsync()`

2. **Navigating back to previous page**:
   - ✅ CORRECT: `await MauiControls.Shell.Current.GoToAsync("..")`
   - ❌ WRONG: `await Navigation.PopAsync()`

3. **Never use the Navigation service**: The `Navigation` property (INavigation) is for NavigationPage-based apps. This app uses Shell, so always use `MauiControls.Shell.Current.GoToAsync()`.

## Page Refresh Pattern

**CRITICAL: Use .OnAppearing() to reload data when returning to a page!**

When a page needs to refresh its data after navigating back from another page (e.g., after creating/editing an item), use the `.OnAppearing()` extension method on the ContentPage:

```csharp
public override VisualNode Render()
{
    return ContentPage("Page Title",
        Grid(
            // ... page content
        )
    )
    .OnAppearing(LoadData);  // Reload data each time page appears
}

private async void LoadData()
{
    // Fetch fresh data from repository/service
    var data = await _repository.GetDataAsync();
    SetState(s => s.Data = data);
}
```

**Pattern examples in codebase**:
- `DashboardPage.cs`: `.OnAppearing(LoadOrRefreshDataAsync)`
- `WritingPage.cs`: `.OnAppearing(LoadVocabulary)`
- `UserProfilePage.cs`: `.OnAppearing(LoadProfile)`
- `ListSkillProfilesPage.cs`: `.OnAppearing(LoadProfiles)`

**When to use OnAppearing**:
- After creating/editing items in child pages
- After deleting items that need list refresh
- When data might have changed while on other pages
- For pages that show user-specific dynamic content

## Command Line Tool Usage Guidelines

**CRITICAL: Avoid triggering directory access prompts by following these patterns:**

1. **NEVER create temporary files** outside the working directory:
   - ❌ WRONG: `cat > /tmp/summary.md`
   - ✅ CORRECT: Output directly or use files in the working directory

2. **GREP usage - avoid path interpretation issues**:
   - When grepping for strings with special characters or patterns, be careful with comment syntax
   - The pattern `grep -v "//"` can be interpreted as trying to access the `//` directory
   - ❌ WRONG: `grep '"text"' file | grep -v "//" | grep -v "pattern"`
   - ✅ CORRECT: Use single grep with extended regex: `grep -E '"text"' file | grep -vE '(//|pattern)'`
   - ✅ CORRECT: Or escape properly: `grep -v '\\/\\/'`
   - ✅ CORRECT: Or use alternative patterns that don't look like paths

3. **Safe grep patterns for finding hardcoded strings**:
   ```bash
   # Find quoted strings (basic)
   grep -n '"[A-Za-z]' file.cs
   
   # Exclude patterns safely with -v in separate command
   grep '"text"' file.cs | grep -v 'namespace' | grep -v 'using'
   
   # Or use awk/sed if complex filtering needed
   grep '"text"' file.cs | awk '!/namespace/ && !/using/'
   ```

4. **When outputting summaries**: 
   - Write to stdout, not temp files
   - Or write to a file in the workspace if persistence is needed
   - Never use `/tmp/` or other system directories

5. **Working directory awareness**:
   - You are always in `/Users/davidortinau/work/SentenceStudio`
   - All paths should be relative to this or absolute within this tree
   - Use `find src/` not `find /tmp/`

## Debugging and Logging by Platform

**CRITICAL: Always use ILogger for production code!**

**MUST DO**: Use `ILogger<T>` for all logging in services, pages, and business logic. Only use `System.Diagnostics.Debug.WriteLine()` for temporary debugging during development. ILogger provides:
- Structured logging with log levels (Debug, Information, Warning, Error, Critical)
- Configuration-based filtering and routing
- Multiple output destinations (console, file, Application Insights, etc.)
- Better performance with compile-time message templates
- Proper integration with .NET logging infrastructure

**IMPORTANT: Logging locations vary by target framework. Use the correct method for each platform:**

### macOS (net10.0-maccatalyst)

**System.Diagnostics.Debug.WriteLine()** output goes to:
- **Xcode Console**: Open Console.app → select your Mac device → filter by process "SentenceStudio"
- **Terminal command**: `log show --predicate 'process == "SentenceStudio"' --last 5m --style compact`
- **VS Code Debug Console**: When running with debugger attached

**File logging** (recommended for complex debugging):
```csharp
var logPath = Path.Combine(FileSystem.AppDataDirectory, "debug.log");
File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} - Log message\n");
// Location: ~/Library/Containers/com.companyname.sentencestudio/Data/Library/Application Support/debug.log
```

### iOS (net10.0-ios)

**System.Diagnostics.Debug.WriteLine()** output goes to:
- **Xcode Console**: Window → Devices and Simulators → select device → View Device Logs
- **Terminal (Simulator)**: `xcrun simctl spawn booted log stream --predicate 'processImagePath contains "SentenceStudio"' --level debug`
- **VS Code Debug Console**: When running with debugger attached

**File logging**:
```csharp
var logPath = Path.Combine(FileSystem.AppDataDirectory, "debug.log");
// Location (device): Cannot directly access, use Xcode → Window → Devices → Download Container
// Location (simulator): ~/Library/Developer/CoreSimulator/Devices/[UUID]/data/Containers/Data/Application/[UUID]/Library/debug.log
```

### Android (net10.0-android)

**System.Diagnostics.Debug.WriteLine()** output goes to:
- **Android Studio Logcat**: View → Tool Windows → Logcat, filter by package name
- **Terminal command**: `adb logcat | grep SentenceStudio`
- **VS Code Debug Console**: When running with debugger attached

**File logging**:
```csharp
var logPath = Path.Combine(FileSystem.AppDataDirectory, "debug.log");
// Location: /data/data/com.companyname.sentencestudio/files/debug.log
// Access via: adb shell run-as com.companyname.sentencestudio cat files/debug.log
```

### Windows (net10.0-windows)

**System.Diagnostics.Debug.WriteLine()** output goes to:
- **VS Code Debug Console**: When running with debugger attached
- **DebugView** (SysInternals): Download from Microsoft, shows all debug output
- **Event Viewer**: Windows Logs → Application (if using EventLog)

**File logging**:
```csharp
var logPath = Path.Combine(FileSystem.AppDataDirectory, "debug.log");
// Location: C:\Users\[Username]\AppData\Local\Packages\[PackageId]\LocalState\debug.log
```

### Best Practices for Cross-Platform Logging

1. **Use emoji prefixes** for easy log scanning:
   - 🚀 Process start
   - ✅ Success
   - ❌ Errors
   - ⚠️ Warnings
   - 📏 Measurements
   - 🔧 Configuration

2. **Include timestamps and context**:
   ```csharp
   System.Diagnostics.Debug.WriteLine($"⏱️ {DateTime.Now:HH:mm:ss.fff} [Component] Action: value={data}");
   ```

3. **For complex debugging, use file logging**:
   - Persists across app restarts
   - Can be read at any time
   - Works consistently across all platforms
   - Use `FileSystem.AppDataDirectory` for cross-platform compatibility

4. **Platform-specific debugging tools**:
   - **macOS**: Console.app is most reliable
   - **iOS**: Xcode Console for device debugging
   - **Android**: adb logcat is standard
   - **Windows**: DebugView for system-wide debug output

## Page Usage Contexts and Entry Points

**CRITICAL: Understand that pages have MULTIPLE usage contexts!**

Activity pages (VocabularyQuizPage, ReadingPage, ShadowingPage, etc.) are used in at least TWO major ways:

### **1. From Daily Plan (Structured Learning)**
- **Entry Point**: Dashboard → Today's Plan → Click activity
- **Props.FromTodaysPlan**: `true`
- **Props.PlanItemId**: Set (for progress tracking)
- **Activity Timer**: Enabled
- **Goal**: Complete specific planned activity (e.g., "Review 20 vocabulary words")
- **Scope**: DeterministicPlanBuilder pre-selected content (20 words, specific resource)
- **Progress Tracking**: Updates plan completion status
- **User Expectation**: "I'm working on my daily plan"

### **2. Manual Resource Selection (Free Practice)**
- **Entry Point**: Resources → Browse → Select resource → Choose activity
- **Props.FromTodaysPlan**: `false`
- **Props.PlanItemId**: `null`
- **Activity Timer**: Disabled
- **Goal**: Review ALL due content from selected resource
- **Scope**: All vocabulary/content in the resource (filtered by SRS due dates)
- **Progress Tracking**: Updates word/content progress, but NOT plan completion
- **User Expectation**: "I'm practicing this specific resource"

### **Potential Future Contexts**
- **Study Mode** (custom sessions): User sets own goals (e.g., "Review 50 words")
- **Test Preparation**: Focus on weak areas across multiple resources
- **Challenge Mode**: Time-limited, gamified sessions
- **Review Mode**: Revisit previously learned content

### **Design Implications**

**When building page UI/logic, ALWAYS consider:**

1. **Progress Indicators**:
   - From Plan: Show progress toward plan goal ("5 of 20 words")
   - Manual: Show progress through selected content ("5 of 12 due words")

2. **Session Size**:
   - From Plan: Use predetermined size (DeterministicPlanBuilder's 20 words)
   - Manual: Use all due/available content (could be 5, could be 50)

3. **Completion Behavior**:
   - From Plan: Mark plan item complete, return to dashboard
   - Manual: Show summary, offer to continue or return to resources

4. **Timer Display**:
   - From Plan: Show ActivityTimerBar in TitleView
   - Manual: No timer needed

5. **Content Selection**:
   - From Plan: Pre-filtered by planner (specific resources, word count)
   - Manual: User's full resource selection, filtered by SRS

**MAINTENANCE REQUIREMENT:**
When adding NEW usage contexts (e.g., "Challenge Mode"), update:
1. This section in copilot-instructions.md
2. Page header documentation
3. ActivityProps class (if new properties needed)
4. Page logic to handle new context appropriately

**CODE REVIEW CHECKLIST:**
- [ ] Does this change work for BOTH daily plan AND manual contexts?
- [ ] Are progress indicators context-aware?
- [ ] Is session sizing appropriate for both contexts?
- [ ] Does completion behavior match user expectations?
- [ ] Are new usage contexts documented?

## Localization Guidelines

**CRITICAL: Always use string interpolation with LocalizationManager!**

**IMPORTANT: Use enums over string keys for type safety!**

When working with localized content that has associated enums (like `PlanActivityType`), always prefer using the enum to determine the localization key rather than storing string keys. This avoids mismatches between AI-generated snake_case keys (e.g., "plan_item_vocab_review_title") and actual PascalCase resource keys (e.g., "PlanItemVocabReviewTitle").

✅ CORRECT:
```csharp
string GetActivityTitle(DailyPlanItem item)
{
    return item.ActivityType switch
    {
        PlanActivityType.VocabularyReview => $"{_localize["PlanItemVocabReviewTitle"]}",
        PlanActivityType.Reading => $"{_localize["PlanItemReadingTitle"]}",
        // ... use the enum, not item.TitleKey string
    };
}
```

❌ WRONG:
```csharp
// Don't rely on TitleKey strings from AI-generated data
return $"{_localize[item.TitleKey]}"; // May not match resource file format
```

1. **NEVER access localized strings without string interpolation**:

   ❌ WRONG:
   ```csharp
   Label(_localize["Key"])  // Returns object, not string!
   Button(_localize["ButtonText"])  // Returns object, not string!
   ContentPage(_localize["Title"], ...)  // Returns object, not string!
   ```
   
   ✅ CORRECT:
   ```csharp
   Label($"{_localize["Key"]}")
   Button($"{_localize["ButtonText"]}")
   ContentPage($"{_localize["Title"]}", ...)
   ```

2. **Use Button/ImageButton for buttons**: Don't compose buttons from Border + Label unless there's a compelling reason MauiReactor's Button doesn't meet your needs.

   ❌ WRONG:
   ```csharp
   Border(
       Label($"{_localize["ButtonText"]}")
           .Center()
   )
   .BackgroundColor(MyTheme.ButtonBackground)
   .OnTapped(() => DoSomething())
   ```
   
   ✅ CORRECT:
   ```csharp
   Button($"{_localize["ButtonText"]}")
       .BackgroundColor(MyTheme.ButtonBackground)
       .OnTapped(() => DoSomething())
   ```

3. **LocalizationManager pattern**: Ensure components have the localization manager property:
   ```csharp
   LocalizationManager _localize => LocalizationManager.Instance;
   ```

4. **For complete localization guidelines**, refer to `.github/agents/localize.agent.md` which includes:
   - Resource file format and naming conventions
   - Korean translation guidelines
   - String interpolation patterns
   - Common translation reference
