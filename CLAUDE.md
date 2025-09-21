# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

### Prerequisites

- Install .NET 9.0 SDK or later (targeting .NET 10.0 frameworks)
- Install MAUI workloads: `dotnet workload install maui`
- Configure API keys (see API Keys Configuration section)

### Build Commands

**CRITICAL**: Always specify target framework when building. Never use `dotnet build` alone.

```bash
# Navigate to source directory first
cd src

# Build for specific platforms
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-android
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-ios
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0
```

### Running the Application

**IMPORTANT**: Never use `dotnet run` for MAUI apps. Use build with Run target:

```bash
# Desktop (macOS Catalyst)
dotnet build -t:Run -f net10.0-maccatalyst SentenceStudio/SentenceStudio.csproj

# Desktop (Windows)
dotnet build -t:Run -f net10.0-windows10.0.19041.0 SentenceStudio/SentenceStudio.csproj
```

### Dependency Management

```bash
# Restore NuGet packages
dotnet restore

# Update workloads if needed
dotnet workload repair
```

## API Keys Configuration

### Required API Keys

1. **OpenAI API Key** - For AI-powered language learning features
2. **ElevenLabs API Key** - For text-to-speech functionality  
3. **HuggingFace Token** (Optional) - For additional AI model access
4. **Syncfusion Key** (Optional) - For premium UI components

### Configuration Setup

Copy template and configure keys:

```bash
cp src/SentenceStudio/appsettings.template.json src/SentenceStudio/appsettings.json
```

For desktop development, use environment variables:

- `AI__OpenAI__ApiKey` - OpenAI API key
- `ElevenLabsKey` - ElevenLabs API key  
- `SyncfusionKey` - Syncfusion license key

**Security**: Never commit `appsettings.json` with real API keys - it's gitignored.

## Architecture Overview

### Technology Stack

- **Framework**: .NET MAUI (.NET 10.0) for cross-platform mobile/desktop
- **UI Library**: MauiReactor (Reactor.Maui) using Model-View-Update (MVU) pattern
- **AI Integration**: Microsoft.Extensions.AI with OpenAI backend
- **Text-to-Speech**: ElevenLabs API
- **Database**: SQLite with CoreSync for synchronization
- **Templates**: Scriban for dynamic prompt generation

### Project Structure

```
src/
├── SentenceStudio/              # Main MAUI application
│   ├── Pages/                   # UI pages using MauiReactor MVU
│   ├── Services/                # Business logic and AI integration
│   ├── Data/                    # Repository pattern for data access
│   ├── Models/                  # DTOs and domain models
│   └── Resources/               # Assets, fonts, localization
├── SentenceStudio.Shared/       # Shared models and utilities
├── SentenceStudio.Web/          # Web API components
└── SentenceStudio.AppHost/      # Application hosting (.NET Aspire)
```

### Key Architecture Patterns

#### MauiReactor MVU Pattern

- Components extend `Component` base class
- UI declared using fluent syntax methods
- State management through `[Param]` and `[Inject]` attributes
- No XAML - pure C# UI definition

#### Data Layer

- Repository pattern for data access (`*Repository.cs` in Data/)
- SQLite database with Entity Framework Core
- CoreSync integration for cross-device synchronization
- Database path: `FileSystem.AppDataDirectory/sstudio.db3`

#### AI Service Integration

- `AiService.cs` - Main AI orchestration using Microsoft.Extensions.AI
- Strongly-typed DTOs with `[Description]` attributes for AI context
- Template-based prompts using Scriban (Resources/Raw/*.scriban-txt)
- Automatic JSON serialization/deserialization

## Development Guidelines

### MauiReactor UI Guidelines

**CRITICAL**: Follow proper MauiReactor patterns:

1. **No Unnecessary Wrappers**: Never wrap render method calls in extra containers

   ```csharp
   // ❌ Wrong
   VStack(RenderHeader()).Padding(16).GridRow(0)
   
   // ✅ Correct - apply properties inside render methods
   RenderHeader() // Properties applied inside RenderHeader()
   ```

2. **Grid Layout Syntax**:

   ```csharp
   Grid(rows: "Auto,Auto,*", columns: "*",
       RenderHeader(),
       RenderBody(), 
       RenderFooter()
   )
   ```

3. **Scrolling Controls**: Never put CollectionView in unlimited vertical containers (VStack)

   ```csharp
   // ✅ Use Grid with constrained rows
   Grid(rows: "Auto,*", columns: "*",
       RenderHeader().GridRow(0),
       CollectionView().GridRow(1) // Constrained by star-sized row
   )
   ```

4. **Layout Property Mappings**:
   - Use `VStart()` instead of `Top()`
   - Use `VEnd()` instead of `Bottom()`
   - Use `HStart()` and `HEnd()` instead of `Start()` and `End()`

5. **Third-Party Controls - ALWAYS Use Built-in Features**:
   **CRITICAL**: When using Syncfusion Charts or other third-party controls, ALWAYS use their built-in features instead of creating custom implementations:

   ```csharp
   // ❌ WRONG - Never create custom legends or data labels
   HStack(
       chartData.Select(item =>
           HStack(Border().BackgroundColor(item.Color), Label(item.Name))
       )
   ) // Don't create custom legends!
   
   // ✅ CORRECT - Use built-in chart features
   var chart = new SfCircularChart();
   chart.Legend = new ChartLegend() { IsVisible = true }; // Built-in legend
   series.DataLabelSettings = new CircularDataLabelSettings() 
   { 
       LabelPosition = ChartDataLabelPosition.Outside // Built-in data labels
   };
   series.CenterView = centerContent; // Built-in center view
   ```

   **Required Reading**: ALWAYS read the official documentation for third-party controls to understand their built-in capabilities before implementing anything custom. Chart controls have legends, data labels, tooltips, center views, and other features - use them!

6. **Documentation First Approach**: Before implementing any visualization or complex UI:
   - Read the control's official documentation completely
   - Look for built-in features like legends, labels, center views, etc.
   - Only create custom solutions if the built-in features are insufficient

### Font Configuration

Registered fonts (in MauiProgram.cs):

- `SegoeBold`, `SegoeRegular`, `SegoeSemibold`, `SegoeSemilight`
- `Yeonsung` (Korean font: bm_yeonsung.ttf)
- `FontAwesome.FontFamily` (fa_solid.ttf)
- `FluentUI.FontFamily` (FluentSystemIcons-Regular.ttf)

### AI/Prompt Development

1. **Use [Description] Attributes**: Microsoft.Extensions.AI automatically uses these for context

   ```csharp
   public class ExampleDto
   {
       [Description("Clear description of what this property should contain")]
       public string PropertyName { get; set; } = string.Empty;
   }
   ```

2. **Clean Scriban Templates**: Focus on business logic, not JSON structure
3. **No Manual JSON Formatting**: Library handles serialization automatically
4. **Connectivity Checks**: Always check `Connectivity.NetworkAccess` before AI calls

### Styling Guidelines

- Use centralized styles from theme system (MyTheme.cs, ReactorTheme)
- Never use colors for text readability - use theme-appropriate colors
- Apply styles at theme level, not component level unless specifically needed
- Accessibility: Use colored backgrounds/borders/icons, not colored text

### Database Development

- Database location: `Constants.DatabasePath` (FileSystem.AppDataDirectory/sstudio.db3)
- Use Entity Framework Core migrations for schema changes
- Repository pattern for all data access
- CoreSync enabled for cross-device synchronization

## Common Development Tasks

### Adding New Learning Activities

1. Create page in `Pages/` directory extending `Component`
2. Register route in `MauiProgram.RegisterRoutes()`
3. Add navigation in `AppShell.cs` if needed
4. Create corresponding service in `Services/` for AI integration
5. Add Scriban template in `Resources/Raw/` for prompts

### AI Service Integration

1. Create strongly-typed DTO with `[Description]` attributes
2. Add Scriban template for prompt generation
3. Use `AiService.SendPrompt<T>()` for API calls
4. Handle connectivity and error scenarios

### Database Schema Changes

1. Create new model in `SentenceStudio.Shared/Models/`
2. Update `ApplicationDbContext.cs`
3. Generate migration: `dotnet ef migrations add MigrationName`
4. Update repositories as needed

## Testing and Quality

### Build Verification

Always run builds for all target platforms before committing:

```bash
cd src
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-android  
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0
```

### Performance Considerations

- Use CollectionView for large datasets (provides virtualization)
- Implement proper disposal patterns for audio/media resources
- Monitor memory usage with large language model responses
- Cache frequently used AI responses when appropriate

## Troubleshooting

### Common Build Issues

- **Missing MAUI workloads**: Run `dotnet workload install maui`
- **Target framework errors**: Always specify `-f` flag with framework
- **API key issues**: Verify environment variables or appsettings.json

### Runtime Issues  

- **AI features not working**: Check API keys and internet connectivity
- **Database errors**: Verify SQLite file permissions and CoreSync configuration
- **Audio issues**: Check platform-specific audio permissions and ElevenLabs key

### Platform-Specific Notes

- **iOS/macOS**: Requires Xcode and proper provisioning profiles
- **Android**: Requires Android SDK and USB debugging enabled
- **Windows**: Requires Windows App SDK and appropriate Windows version

## Documentation Resources

Context7 MCP provides comprehensive documentation for the technology stack:

- **.NET MAUI**: <https://context7.com/dotnet/maui/llms.txt>
- **Community Toolkit for .NET MAUI**: <https://context7.com/communitytoolkit/maui.git/llms.txt>
- **MauiReactor**: <https://context7.com/adospace/reactorui-maui/llms.txt>
- **SkiaSharp**: <https://context7.com/mono/skiasharp/llms.txt>
- **ElevenLabs API Official Docs**: <https://context7.com/elevenlabs/elevenlabs-docs/llms.txt>
- **ElevenLabs-DotNet SDK**: <https://context7.com/rageagainstthepixel/elevenlabs-dotnet/llms.txt>
