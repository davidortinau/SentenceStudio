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

## Squad-local skills (project-specific, in `.squad/skills/`)

These live in the repo and are **invisible to general-purpose Copilot agents** unless explicitly loaded. When spawning Squad agents (Wash, Kaylee, Scribe, etc.), reference these by path so the agent reads them before working.

| Skill | Use For |
|-------|---------|
| `maui-ios-dx24-install` | iOS publish to DX24 — preemptive wake/unlock + NWError 57 retry-once recipe |
| `blazor-activity-layout-shell` | Building a new Blazor activity page — copy VocabQuiz shell verbatim, anti-patterns from publishes #5–#9 |
| `agent-progress-diagnostic` | Decide whether a long-running background agent is hung vs. making progress |
| `maui-devflow-blazor-hybrid` | DevFlow + Blazor Hybrid integration, tunnel issues |
| `dotnet-sdk-detection` | 4-layer SDK selection diagnostic before claiming "SDK isn't installed" |
| `ef-dual-provider-migrations` | EF migrations affecting both PostgreSQL (API) and SQLite (mobile) |
| `single-flight-async` | SemaphoreSlim + cached Task<T>? to collapse duplicate in-flight async ops |
| `async-single-flight-testing` | xUnit pattern for testing exactly-one-call semantics under concurrent load |
| `activity-audio-playback` | Audio playback patterns in activity pages |
| `activity-audit-checklist` | Pre-ship audit checklist for activity pages |
| `activity-picker-gating` | Activity picker visibility/gating rules |
| `adding-smart-resources` | Adding smart-resource integrations |
| `aspire-maui-bundle-shim` | Aspire + MAUI bundle shim |
| `aspire-orphan-recovery` | Recover orphaned Aspire processes |
| `aspnetcore-azure-monitor` | ASP.NET Core + Azure Monitor wiring |
| `auth-e2e-testing` | End-to-end auth testing |
| `azure-predeploy-validation` | Pre-deploy validation gates for Azure |
| `blazor-hybrid-firstrender-jsinit` | First-render JS init in Blazor Hybrid |
| `blazor-localization` | Localization in Blazor Hybrid pages |
| `blazor-nav-state-preservation` | Navigation state preservation in Blazor |
| `blazor-readonly-mode` | Readonly mode patterns |
| `empty-table-startup-diagnostic` | Diagnose empty-table-on-startup issues |
| `grader-override-pattern` | Grader override pattern for activities |
| `maui-azure-monitor` | MAUI + Azure Monitor wiring |
| `mcp-tool-discovery` | MCP tool discovery patterns |
| `number-content-seeding` | Number-content seeding for NumberDrill |
| `paired-prompt-ui` | Paired-prompt UI pattern |
| `project-conventions` | Repo-wide conventions |
| `resource-id-decoupling` / `resourceid-decoupling` | Resource ID decoupling |
| `sqlite-migration-generation` | Generate SQLite migrations |
| `sqlite-migration-history-reconcile` | Reconcile SQLite migration history |
| `structured-import-results` | Structured import result handling |

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
