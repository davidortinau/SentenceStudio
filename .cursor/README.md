# SentenceStudio Project Guidelines

Welcome to the SentenceStudio codebase! This project is a .NET MAUI app targeting mobile and desktop, using the MauiReactor (Reactor.Maui) MVU library for UI. Below are essential guidelines and conventions for working in this repository.

---

## Build & Run Instructions

- **Always specify a Target Framework Moniker (TFM) when building:**
  ```sh
  dotnet build -f net10.0-maccatalyst
  ```
- **To run the app, use:**
  ```sh
  dotnet build -t:Run -f net10.0-maccatalyst
  ```
  > **Note:** Do NOT use `dotnet run` for .NET MAUI apps—it does not work.

---

## MauiReactor UI Guidelines

- **No Unnecessary Layout Wrappers:**
  - Do not wrap render method calls in extra `VStack`, `HStack`, or other containers just to apply properties like `Padding` or `GridRow`. Apply these properties inside the render methods.
- **Grid Syntax:**
  - Use MauiReactor's inline grid syntax:
    ```csharp
    Grid(rows: "Auto,Auto,*", columns: "*",
        RenderHeader(),
        RenderBody(),
        RenderFooter()
    )
    ```
- **Scrolling Controls:**
  - Never put vertically scrolling controls (like `CollectionView`) inside containers that allow unlimited vertical expansion (e.g., `VStack`). Use a grid row with star sizing to constrain them.
- **Performance:**
  - Use `CollectionView` for large datasets to benefit from virtualization.
- **Layout Properties:**
  - Apply `GridRow`, `Padding`, and other layout properties directly to the root element of each render method.
- **C# Markup to MauiReactor Conversion:**
  - Use `VStart()` instead of `Top()`
  - Use `VEnd()` instead of `Bottom()`
  - Use `HStart()` and `HEnd()` instead of `Start()` and `End()`

---

## Styling & Accessibility

- **Centralized Styles:**
  - Prefer using styles from `ApplicationTheme.cs` for text colors, backgrounds, fonts, etc.
  - Only override styles at the component level for specific needs.
- **Accessibility:**
  - Never use color alone for text readability. Use colored backgrounds, borders, or icons instead.
  - Text should always use theme-appropriate colors (e.g., `ApplicationTheme.DarkOnLightBackground`).

---

## AI Prompts & DTOs (Microsoft.Extensions.AI)

- **[Description] Attributes:**
  - Use `[Description]` attributes on DTO properties to guide AI prompt generation and context.
- **No Manual JSON Formatting:**
  - Do not specify JSON structure in Scriban templates. The library handles serialization/deserialization.
- **No [JsonPropertyName] Needed:**
  - Only use `[JsonPropertyName]` if a specific JSON field name is required.
- **Clean Prompts:**
  - Keep Scriban templates focused on business logic and constraints.

**Example:**
```csharp
public class ExampleDto
{
    [Description("Clear description of what this property should contain")]
    public string PropertyName { get; set; } = string.Empty;
}
```

---

## Documentation & References

- .NET MAUI: https://context7.com/dotnet/maui/llms.txt
- Community Toolkit for .NET MAUI: https://context7.com/communitytoolkit/maui.git/llms.txt
- MauiReactor: https://context7.com/adospace/reactorui-maui/llms.txt
- ElevenLabs API: https://context7.com/elevenlabs/elevenlabs-docs/llms.txt
- ElevenLabs-DotNet SDK: https://context7.com/rageagainstthepixel/elevenlabs-dotnet/llms.txt
- For .NET, Windows, or Microsoft features/APIs, always check Microsoft Docs (MS Learn) for the latest info.

---

## Additional Notes

- For CoreSync work, see the sample project: https://github.com/adospace/mauireactor-core-sync
- Keep code maintainable and consistent with these guidelines for best results. 