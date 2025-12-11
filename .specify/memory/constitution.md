<!--
═══════════════════════════════════════════════════════════════════════════════
SYNC IMPACT REPORT - Constitution v1.0.0
═══════════════════════════════════════════════════════════════════════════════

VERSION CHANGE: Initial ratification → v1.0.0
RATIONALE: First formal constitution for SentenceStudio project

PRINCIPLES ESTABLISHED:
  1. User-Centric AI-Powered Learning - Personalized language learning with AI
  2. Cross-Platform Native Experience - .NET MAUI with platform-specific optimizations
  3. MauiReactor MVU Architecture - Declarative UI with Model-View-Update pattern
  4. Theme-First UI Development - Centralized styling through MyTheme.cs
  5. Localization by Default - Korean/English support from day one
  6. Observability & Debugging - ILogger, platform-specific debug outputs
  7. Documentation in docs/ - All documentation in dedicated folder

ADDED SECTIONS:
  - Technology Stack & Constraints
  - Development Workflow & Quality Gates

TEMPLATES REQUIRING UPDATES:
  ✅ plan-template.md - Added constitution check gates
  ✅ spec-template.md - Aligned with cross-platform requirements
  ✅ tasks-template.md - Updated for MAUI project structure

FOLLOW-UP TODOS: None - all placeholders filled

NEXT STEPS:
  - Review constitution with team
  - Ensure all developers have read `.github/copilot-instructions.md`
  - Update CI/CD pipelines to align with build requirements

═══════════════════════════════════════════════════════════════════════════════
-->

# SentenceStudio Constitution

## Core Principles

### I. User-Centric AI-Powered Learning

SentenceStudio MUST enable personalized language learning through AI-powered exercises that adapt to user-provided curriculum and vocabulary.

**Rules:**
- Users MUST be able to import custom vocabulary (CSV) and skills (TXT) files
- AI-powered activities MUST provide immediate, contextual feedback
- Learning content MUST be organized around user-defined resources and themes
- Progress tracking MUST respect Spaced Repetition System (SRS) algorithms
- AI prompts MUST use Scriban templates with DTOs leveraging `[Description]` attributes
- Microsoft.Extensions.AI MUST handle serialization automatically (no manual JSON formatting in prompts)

**Rationale:** The core value proposition is "bring your own curriculum" - users should control what they learn, while AI enhances how they learn it.

### II. Cross-Platform Native Experience

SentenceStudio MUST deliver native-quality experiences on iOS, Android, macOS, and Windows using .NET MAUI.

**Rules:**
- All features MUST be tested on all four target platforms (iOS, Android, macOS, Windows)
- Platform-specific APIs MUST be used when generic abstractions compromise user experience
- Build commands MUST specify Target Framework Moniker (TFM): `dotnet build -f net10.0-maccatalyst`
- Run commands MUST use `dotnet build -t:Run -f <TFM>` (NEVER `dotnet run`)
- Minimum platform versions: iOS 12.2, Android API 21, macOS 15.0, Windows 10.0.17763.0
- Offline functionality MUST be preserved (SQLite local storage with optional CoreSync)

**Rationale:** Users expect native performance and platform conventions. Cross-platform code must not compromise platform-specific quality.

### III. MauiReactor MVU Architecture

UI MUST be expressed using MauiReactor's Model-View-Update (MVU) pattern with fluent method chains.

**Rules:**
- Use semantic alignment: `.HStart()`, `.HCenter()`, `.HEnd()`, `.VStart()`, `.VCenter()`, `.VEnd()`, `.Center()`
- NEVER use `HorizontalOptions` or `VerticalOptions` (use semantic methods instead)
- NEVER use `FillAndExpand` (legacy XAML pattern)
- Prefer string concatenation over multiple Labels: `Label($"🎯 {variable}")` not `HStack(Label("🎯"), Label(variable))`
- Avoid unnecessary wrappers (Border, VStack, HStack) unless providing visual purpose
- ScrollView/CollectionView MUST be constrained (use Grid with star-sized rows, not VStack)
- Grid syntax: `Grid(rows: "Auto,Auto,*", columns: "*", ...)`
- Apply GridRow/Padding/layout properties inside render methods, not by wrapping calls
- ContentPage MUST have single child (use Grid for multiple elements + overlays like SfBottomSheet)

**Rationale:** MauiReactor provides cleaner, more maintainable code than XAML. Consistent patterns prevent layout bugs and improve performance.

### IV. Theme-First UI Development

Visual styling MUST be centralized in `MyTheme.cs` using `.ThemeKey()` method for consistent design.

**Rules:**
- Use `.ThemeKey(MyTheme.Primary)` for buttons, `.ThemeKey(MyTheme.Title1)` for labels, etc.
- Available theme keys: Primary/Secondary/Danger (buttons), Title1/Title2/Body1/Caption1 (labels), CardStyle/InputWrapper (borders)
- When theme keys unavailable, use theme constants: `MyTheme.PrimaryText`, `MyTheme.Size160`, `MyTheme.Spacing80`
- NEVER hardcode colors, font sizes, or spacing values
- NEVER use color for text readability (accessibility violation) - use colored backgrounds/borders/icons instead
- Text colors MUST use theme-appropriate values: `MyTheme.DarkOnLightBackground`, `MyTheme.LightOnDarkBackground`

**Rationale:** Centralized theming ensures design consistency, simplifies rebranding, and maintains accessibility standards.

### V. Localization by Default

All user-facing strings MUST use `LocalizationManager` with string interpolation: `$"{_localize["Key"]}"`.

**Rules:**
- ALWAYS wrap localization keys in string interpolation (returns object without it)
- Pattern: `LocalizationManager _localize => LocalizationManager.Instance;`
- Resource files: `Resources/Strings/Resources.<lang>.resx` (English default, Korean supported)
- For enums with associated text (like `PlanActivityType`), use enum to determine key (not stored string keys)
- Use `Button()` not `Border() + Label()` for buttons unless compelling reason
- For ContentPage title: `ContentPage($"{_localize["Title"]}", ...)`
- Refer to `.github/agents/localize.agent.md` for translation guidelines

**Rationale:** Internationalization must be first-class, not retrofitted. Korean translation requirements demand disciplined localization practices.

### VI. Observability & Debugging

Logging MUST use `ILogger<T>` for production code; platform-specific debug output varies by TFM.

**Rules:**
- Production: Use `ILogger<T>` with structured logging (Debug/Information/Warning/Error/Critical levels)
- Temporary debugging: `System.Diagnostics.Debug.WriteLine()` acceptable during development
- macOS: Console.app or `log show --predicate 'process == "SentenceStudio"' --last 5m`
- iOS: Xcode Console or `xcrun simctl spawn booted log stream`
- Android: `adb logcat | grep SentenceStudio`
- Windows: VS Code Debug Console or DebugView (SysInternals)
- File logging for complex debugging: `Path.Combine(FileSystem.AppDataDirectory, "debug.log")`
- Use emoji prefixes: 🚀 start, ✅ success, ❌ error, ⚠️ warning, 📏 measurement, 🔧 config

**Rationale:** Effective debugging requires platform-appropriate tools. ILogger provides production-grade observability with centralized configuration.

### VII. Documentation in docs/

All documentation (summaries, guides, technical specs) MUST be placed in the `docs/` folder at repository root.

**Rules:**
- NEVER create markdown files (plans, notes, tracking) at repository root
- Feature specs: `docs/specs/<feature-name>/`
- Implementation guides: `docs/<feature>-guide.md`
- Architecture docs: `docs/specs/`
- Examples: `docs/examples/`
- Development guidance: `.github/copilot-instructions.md` (not docs/)

**Rationale:** Centralized documentation improves discoverability and maintains clean repository structure.

## Technology Stack & Constraints

### Mandatory Stack

- **.NET 10.0** (targeting net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)
- **MAUI 10.0.20** or later with workloads installed
- **MauiReactor** (Reactor.Maui) for MVU architecture
- **SQLite** for local data persistence
- **CoreSync** for optional cross-device synchronization
- **OpenAI API** for AI-powered learning features (Microsoft.Extensions.AI library)
- **ElevenLabs API** for text-to-speech/shadowing exercises
- **Scriban** for AI prompt templating
- **Syncfusion MAUI** for advanced UI components (SfBottomSheet, etc.)

### API Keys & Configuration

- API keys MUST be stored in `appsettings.json` (gitignored, template: `appsettings.template.json`)
- Required keys: OpenAI, ElevenLabs, HuggingFace (optional), Syncfusion (optional)
- NEVER commit real API keys to version control
- Environment-specific configuration MUST use standard .NET configuration patterns

### Performance & Scale Constraints

- App startup MUST complete within 3 seconds on mid-range devices
- UI interactions MUST respond within 100ms (perceived instant feedback)
- CollectionView MUST use virtualization for lists exceeding 50 items
- Database queries MUST be async (never block UI thread)
- Images MUST be optimized for target resolution (avoid loading full-res unnecessarily)
- AI API calls MUST show loading indicators for operations exceeding 500ms

### Security & Privacy

- User data MUST be stored locally in SQLite (user controls data)
- CoreSync synchronization MUST be opt-in
- API keys MUST NOT be hardcoded (use configuration)
- Sensitive data (API keys) MUST NOT be logged
- Error messages MUST NOT expose internal implementation details to users

## Development Workflow & Quality Gates

### Build & Run Commands

**Build (all platforms):**
```bash
cd src
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-android
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-ios
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-windows10.0.19041.0
```

**Run (desktop):**
```bash
dotnet build -t:Run -f net10.0-maccatalyst SentenceStudio/SentenceStudio.csproj
```

### Quality Gates

**Before Feature Development:**
- Spec MUST be written in `docs/specs/<feature>/spec.md` following spec-template.md
- User stories MUST be prioritized (P1, P2, P3) and independently testable
- Implementation plan MUST be created using plan-template.md
- Constitution check MUST be performed (verify alignment with all principles)

**During Development:**
- Code MUST follow MauiReactor patterns (no `HorizontalOptions`, use semantic methods)
- UI MUST use `.ThemeKey()` or theme constants (no hardcoded styles)
- Strings MUST use `$"{_localize["Key"]}"` pattern
- Logging MUST use `ILogger<T>` (not Debug.WriteLine in production code)
- Changes MUST work on all target platforms (iOS, Android, macOS, Windows)

**Before Commit:**
- Build MUST succeed on at least one platform
- No compiler warnings for new/modified code (NoWarn list exists for legacy issues)
- Localization keys MUST exist in resource files
- Documentation MUST be updated if feature affects user-facing behavior

**Before PR/Merge:**
- Feature MUST be tested on all four target platforms
- Code review MUST verify constitution compliance
- Breaking changes MUST be documented
- User-facing changes MUST have updated screenshots/documentation

### Troubleshooting Workflow

**Order of Investigation:**
1. Check GitHub issues in repository search order:
   - SentenceStudio (this repo)
   - adospace/reactorui-maui
   - dotnet/maui
   - Related dependencies
2. Search Microsoft Learn documentation (use `microsoft_docs_search` tool)
3. Check platform-specific logs (Console.app, adb logcat, etc.)
4. Consult `.github/copilot-instructions.md` for project-specific guidance

### Custom Agents

The following custom agents exist in `.github/agents/`:
- **localize.agent.md** - Korean/English translation guidance
- **designer.agent.md** - UI/UX design patterns
- **languagetutor.agent.md** - AI prompt engineering for learning activities
- **troubleshooter.agent.md** - Debugging assistance
- **speckit.* agents** - Feature specification workflow (specify, plan, tasks, implement, etc.)

Use appropriate agents for specialized tasks.

## Governance

### Authority

This constitution supersedes all other development practices and guidelines. In case of conflict, constitution principles take precedence.

### Compliance

- All pull requests MUST be reviewed for constitution compliance
- Constitution violations MUST be justified in PR description
- Complexity that violates simplicity principles MUST be documented with rationale
- `.github/copilot-instructions.md` provides runtime development guidance and MUST align with constitution

### Amendments

- Constitution changes require version bump following semantic versioning:
  - **MAJOR**: Backward-incompatible principle removal or redefinition
  - **MINOR**: New principle added or materially expanded guidance
  - **PATCH**: Clarifications, wording, typo fixes, non-semantic refinements
- Amendments MUST update Sync Impact Report (HTML comment at top of constitution.md)
- Dependent templates (plan-template.md, spec-template.md, tasks-template.md) MUST be reviewed for consistency
- Amendment process uses `speckit.constitution` agent

### Review Cycle

- Constitution MUST be reviewed quarterly or after major architectural changes
- Team members MUST acknowledge constitution changes before working on new features
- Violations discovered in existing code SHOULD be tracked as tech debt (not blocking)

**Version**: 1.0.0 | **Ratified**: 2025-12-11 | **Last Amended**: 2025-12-11
