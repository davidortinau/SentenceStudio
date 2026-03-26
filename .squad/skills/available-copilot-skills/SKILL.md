---
name: "available-copilot-skills"
description: "Copilot CLI skills and custom agents available in this environment"
domain: "tooling"
confidence: "high"
source: "environment-audit"
---

## Context

The Copilot CLI environment has 55+ skills and custom agents available. Agents should be aware of these when their work intersects with a skill's domain. The Coordinator routes to skills when relevant.

## Key Skills for This Project

### MAUI Development
| Skill | Use For |
|-------|---------|
| `maui-current-apis` | Always-on guardrail for API currency — prevents deprecated APIs |
| `maui-collectionview` | CollectionView: data display, layouts, selection, grouping |
| `maui-data-binding` | XAML bindings, compiled bindings, MVVM |
| `maui-custom-handlers` | Custom handlers, property mappers, platform-specific views |
| `maui-dependency-injection` | DI registration, lifetime selection, testability |
| `maui-gestures` | Tap, swipe, pan, pinch, drag-and-drop |
| `maui-animations` | View animations, easing, rotation, scale |
| `maui-app-lifecycle` | App states, backgrounding, resume |
| `maui-app-icons-splash` | App icons, splash screens, SVG conversion |
| `maui-deep-linking` | Android App Links, iOS Universal Links |
| `maui-file-handling` | FilePicker, app data storage |
| `maui-geolocation` | GPS/location features |
| `maui-graphics-drawing` | Custom drawing, GraphicsView, canvas |
| `maui-accessibility` | Screen reader, SemanticProperties, VoiceOver/TalkBack |
| `maui-authentication` | WebAuthenticator, OAuth 2.0, social login |
| `maui-bootstrap-theme` | MauiBootstrapTheme styling, StyleClass, theme switching |

### Blazor Hybrid & MauiReactor
| Skill | Use For |
|-------|---------|
| `maui-hot-reload-diagnostics` | Hot Reload troubleshooting (C#, XAML, Blazor) |
| `hotreload-sentinel` | AI-assisted hot reload monitoring and bug reports |

### Aspire & Backend
| Skill | Use For |
|-------|---------|
| `aspire` | Orchestrate Aspire apps — run, stop, debug, manage resources |
| `maui-aspire` | MAUI + Aspire integration, service discovery, HttpClient setup |
| `entra-id-aspire-authentication` | Add Entra ID/Azure AD auth to Aspire apps |
| `entra-id-aspire-provisioning` | Provision Entra ID app registrations |

### Testing & Debugging
| Skill | Use For |
|-------|---------|
| `e2e-testing` | E2E testing via Aspire + Playwright/MAUI debugging |
| `maui-ai-debugging` | Build, deploy, inspect MAUI apps as AI agent |
| `appium-automation` | Cross-platform mobile UI testing |

### Content & Documentation
| Skill | Use For |
|-------|---------|
| `maui-docs-author` | Author .NET MAUI docs in docs-maui repo |
| `dotnet-blog-author` | Draft .NET blog posts |
| `maui-sample-creator` | Create sample projects |
| `changelog-triage` | Triage commits/PRs for docs gaps |

### Custom Agents
| Agent | Use For |
|-------|---------|
| `.NET MAUI Guidance` | Modern controls, XAML best practices, layout/perf rules |
| `MauiReactor Guidance` | MVU, C# fluent UI, state updates, navigation |
| `designer` | UI/UX design, accessibility, theme consistency |
| `language-tutor` | Evidence-based SLA methods for learning features |
| `language-learning-architect` | Exercise design, progress tracking, gamification |
| `troubleshooter` | Structured debugging for .NET MAUI bugs |
| `localize` | String resources and UI localization |
| `csharp-dotnet-janitor` | Code cleanup, modernization, tech debt |

## Usage Pattern

When spawning an agent, include relevant skill references:
```
If .squad/skills/ has relevant SKILL.md files, read them before working.
Relevant Copilot skill: {skill-name} — invoke if needed for domain guidance.
```

## Anti-Patterns

- **Don't invoke skills for simple tasks** — skills add overhead
- **Don't invoke multiple overlapping skills** — pick the most specific one
- **Don't forget maui-current-apis** — it catches deprecated API usage
