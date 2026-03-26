---
name: "mcp-tool-discovery"
description: "MCP server tool discovery and usage patterns for this environment"
domain: "tooling"
confidence: "high"
source: "environment-audit"
---

## Context

This project has multiple MCP (Model Context Protocol) servers configured, providing tools beyond basic file/shell operations.

## Available MCP Servers

### GitHub MCP Server (`github-mcp-server-*`)
**Status:** ✅ Available
**Key tools:**
- `search_code` — Search code across GitHub repos
- `search_issues` / `search_pull_requests` — Find issues and PRs
- `list_issues` / `issue_read` — Read issue details
- `list_pull_requests` / `pull_request_read` — PR details, diffs, reviews
- `get_file_contents` — Read files from GitHub repos
- `list_commits` / `get_commit` — Commit history and diffs
- `actions_list` / `actions_get` / `get_job_logs` — CI/CD workflow status
- `list_copilot_spaces` / `get_copilot_space` — Copilot Spaces
**Use when:** Issue triage, PR review, code search across repos, CI status checks

### Aspire MCP Server (`aspire-*`)
**Status:** ✅ Available
**Key tools:**
- `list_resources` — List running Aspire resources (projects, containers)
- `list_console_logs` — View resource stdout/stderr
- `list_structured_logs` — Query structured log entries
- `list_traces` / `list_trace_structured_logs` — Distributed tracing
- `execute_resource_command` — Start/stop/restart resources
- `list_integrations` — Available Aspire NuGet integrations
- `doctor` — Environment health checks
- `search_docs` / `get_doc` / `list_docs` — Aspire documentation
**Use when:** Running the app, debugging services, checking health, reading Aspire docs

### Playwright MCP Server (`playwright-*`)
**Status:** ✅ Available
**Key tools:**
- `browser_navigate` / `browser_snapshot` / `browser_take_screenshot` — Page interaction
- `browser_click` / `browser_type` / `browser_fill_form` — UI interaction
- `browser_evaluate` / `browser_run_code` — JavaScript execution
- `browser_console_messages` / `browser_network_requests` — Debugging
**Use when:** E2E testing, UI verification, webapp interaction

### Microsoft Learn Docs (`learndocs-*`)
**Status:** ✅ Available
**Key tools:**
- `microsoft_docs_search` — Search MS Learn documentation
- `microsoft_code_sample_search` — Find code samples
- `microsoft_docs_fetch` — Fetch full documentation pages
**Use when:** Researching .NET APIs, MAUI features, Azure services, EF Core patterns

### Context7 (`context7-*`)
**Status:** ✅ Available
**Key tools:**
- `resolve-library-id` — Find library IDs for documentation lookup
- `query-docs` — Query library documentation with code examples
**Available libraries:** .NET MAUI, MauiReactor, Community Toolkit, SkiaSharp, ElevenLabs
**Use when:** Looking up API usage, code examples, library-specific patterns

### Work IQ (`workiq-*`)
**Status:** ✅ Available (requires EULA acceptance)
**Key tools:**
- `ask_work_iq` — Query M365 Copilot for emails, meetings, files
**Use when:** Checking work context, meeting notes, shared files

### Hot Reload Sentinel (`hotreload-sentinel-*`)
**Status:** ✅ Available
**Key tools:**
- `hr_watch_start` / `hr_watch_stop` / `hr_watch_follow` — Monitor hot reload
- `hr_diagnose` / `hr_status` / `hr_report` — Diagnose hot reload issues
- `hr_pending_atoms` / `hr_record_verdict` — Track change confirmations
**Use when:** Debugging hot reload issues, monitoring live development sessions

## Routing Rules

- **Coordinator handles directly:** Simple MCP reads (status checks, single queries)
- **Spawn with MCP context:** When task needs agent expertise AND MCP tools
- **Explore agents never get MCP:** Route MCP work to general-purpose agents
- **Graceful degradation:** If MCP tool missing, fall back to CLI (`gh`, `az`, etc.)

## Anti-Patterns

- **Don't use MCP when CLI is simpler** — `gh issue list` is often faster than MCP for simple queries
- **Don't assume MCP availability** — always degrade gracefully
- **Don't pass MCP context to explore agents** — they can't use it
