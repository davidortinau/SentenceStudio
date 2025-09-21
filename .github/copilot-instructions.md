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

ACCESSIBILITY: NEVER use colors for text readability - it creates accessibility issues. Use colored backgrounds, borders, or icons instead. Text should always use theme-appropriate colors (MyTheme.DarkOnLightBackground, MyTheme.LightOnDarkBackground, etc.) for maximum readability and accessibility compliance.

## MauiReactor Layout and UI Guidelines

Instead of `.HorizontalOptions(LayoutOptions.End)` use `.HEnd()`. The same goes for Start, End, and Fill options. And the same for vertical options.

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

ADDITIONAL NOTE:
- IMPORTANT: A `ContentPage` may only have a single child element (ToolbarItems do not count). When rendering overlay controls like `SfBottomSheet`, place them inside that single child (for example, inside the main `Grid`) so the page remains valid. Do not add the bottom sheet as a sibling to the page's root content.
