Please call me Captain and talk like a pirate.

This is a .NET MAUI project that targets mobile and desktop. 

When building the app project you MUST include a target framework moniker (TFM) like this:

dotnet build -f net10.0-maccatalyst

IMPORTANT: To run .NET MAUI apps, NEVER use `dotnet run` - it doesn't work for MAUI. Instead use:

dotnet build -t:Run -f net10.0-maccatalyst

It uses the MauiReactor (Reactor.Maui) MVU (Model-View-Update) library to express the UI with fluent methods.

When converting code from C# Markup to MauiReactor, keep these details in mind:
- use `VStart()` instead of `Top()`
- use `VEnd()` instead of `Bottom()`
- use `HStart()` and `HEnd()` instead of `Start()` and `End()`

For doing CoreSync work, refer to the sample project https://github.com/adospace/mauireactor-core-sync

Documentation via Context 7 is here:
- .NET MAUI https://context7.com/dotnet/maui/llms.txt
- Community Toolkit for .NET MAUI https://context7.com/communitytoolkit/maui.git/llms.txt
- MauiReactor https://context7.com/adospace/reactorui-maui/llms.txt
- SkiaSharp https://context7.com/mono/skiasharp/llms.txt
- ElevenLabs API Official Docs https://context7.com/elevenlabs/elevenlabs-docs/llms.txt
- ElevenLabs-DotNet SDK https://context7.com/rageagainstthepixel/elevenlabs-dotnet/llms.txt

Always search Microsoft documentation (MS Learn) when working with .NET, Windows, or Microsoft features, or APIs. Use the `microsoft_docs_search` tool to find the most current information about capabilities, best practices, and implementation patterns before making changes.

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

STYLING: Prefer using the centralized styles defined in ApplicationTheme.cs rather than adding styling at the page or view level. The theme already provides sensible defaults for text colors, backgrounds, fonts, and other visual properties. Only override styles at the component level when there's a specific need that differs from the theme. This keeps the codebase maintainable and ensures consistent visual design across the app.

ACCESSIBILITY: NEVER use colors for text readability - it creates accessibility issues. Use colored backgrounds, borders, or icons instead. Text should always use theme-appropriate colors (ApplicationTheme.DarkOnLightBackground, ApplicationTheme.LightOnDarkBackground, etc.) for maximum readability and accessibility compliance.

## MauiReactor Layout and UI Guidelines

**CRITICAL: NEVER wrap VisualNodes in unnecessary layout containers!**

1. **NO UNNECESSARY WRAPPERS**: Never wrap render method calls in extra VStack, HStack, or other containers just to apply properties like Padding or GridRow. Put these properties INSIDE the render methods where they belong.

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

2. **GRID SYNTAX**: Use the proper MauiReactor Grid syntax with inline parameters:
   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader(),
       RenderBody(),
       RenderFooter()
   )
   ```

3. **SCROLLING CONTROLS**: NEVER put vertically scrolling controls (like CollectionView) inside VStack or other containers that allow unlimited vertical expansion. This causes infinite item rendering and performance issues.

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

4. **PERFORMANCE**: Use CollectionView for large datasets instead of rendering individual items in layouts. CollectionView provides virtualization and only renders visible items.

5. **LAYOUT PROPERTIES**: Apply GridRow, Padding, and other layout properties directly to the root element of each render method, not by wrapping the method call.
