---
name: aspire
description: "**WORKFLOW SKILL** - Orchestrates Aspire applications using the Aspire CLI and MCP tools for running, debugging, deploying, and managing distributed apps. USE FOR: aspire run, aspire stop, aspire deploy, start aspire app, aspire describe, list aspire integrations, debug aspire issues, view aspire logs, add aspire resource, aspire dashboard, update aspire apphost, Azure Container Apps deployment, PostgreSQL production configuration. DO NOT USE FOR: non-Aspire .NET apps (use dotnet CLI), container-only deployments (use docker/podman). INVOKES: Aspire MCP tools (list_resources, list_integrations, list_structured_logs, get_doc, search_docs), bash for CLI commands. FOR SINGLE OPERATIONS: Use Aspire MCP tools directly for quick resource status or doc lookups."
---

# Aspire Skill

This repository is set up to use Aspire. Aspire is an orchestrator for the entire application and will take care of configuring dependencies, building, and running the application. The resources that make up the application are defined in `apphost.cs` including application code and external dependencies.

## General recommendations for working with Aspire

1. Before making any changes always run the apphost using `aspire run` and inspect the state of resources to make sure you are building from a known state.
2. Changes to the _apphost.cs_ file will require a restart of the application to take effect.
3. Make changes incrementally and run the aspire application using the `aspire run` command to validate changes.
4. Use the Aspire MCP tools to check the status of resources and debug issues.

## Running Aspire in agent environments

Agent environments may terminate foreground processes when a command finishes. Use detached mode:

```bash
aspire run --detach
```

This starts the AppHost in the background and returns immediately. The CLI will:
- Automatically stop any existing running instance before starting a new one
- Display a summary with the Dashboard URL and resource endpoints

### Running with isolation

The `--isolated` flag starts the AppHost with randomized port numbers and its own copy of user secrets.

```bash
aspire run --detach --isolated
```

Isolation should be used when:
- When AppHosts are started by background agents
- When agents are using source code from a work tree
- There are port conflicts when starting the AppHost without isolation

### Stopping the application

To stop a running AppHost:

```bash
aspire stop
```

This will scan for running AppHosts and stop them gracefully.

### Relaunch rules

- If AppHost code changes, run `aspire run --detach` again to restart with the new code.
- Relaunching is safe: starting a new instance will automatically stop the previous instance.
- Do not attempt to keep multiple instances running.

## Running the application

To run the application run the following command:

```bash
aspire run
```

If there is already an instance of the application running it will prompt to stop the existing instance. You only need to restart the application if code in `apphost.cs` is changed, but if you experience problems it can be useful to reset everything to the starting state.

## Checking resources

To check the status of resources defined in the app model use the _list resources_ tool. This will show you the current state of each resource and if there are any issues. If a resource is not running as expected you can use the _execute resource command_ tool to restart it or perform other actions.

## Listing integrations

IMPORTANT! When a user asks you to add a resource to the app model you should first use the _list integrations_ tool to get a list of the current versions of all the available integrations. You should try to use the version of the integration which aligns with the version of the Aspire.AppHost.Sdk. Some integration versions may have a preview suffix. Once you have identified the correct integration you should always use the _get integration docs_ tool to fetch the latest documentation for the integration and follow the links to get additional guidance.

## Debugging issues

IMPORTANT! Aspire is designed to capture rich logs and telemetry for all resources defined in the app model. Use the following diagnostic tools when debugging issues with the application before making changes to make sure you are focusing on the right things.

1. _list structured logs_; use this tool to get details about structured logs.
2. _list console logs_; use this tool to get details about console logs.
3. _list traces_; use this tool to get details about traces.
4. _list trace structured logs_; use this tool to get logs related to a trace

## Other Aspire MCP tools

1. _select apphost_; use this tool if working with multiple app hosts within a workspace.
2. _list apphosts_; use this tool to get details about active app hosts.

## Playwright CLI

The Playwright CLI has been installed in this repository for browser automation. Use it to perform functional investigations of the resources defined in the app model as you work on the codebase. To get endpoints that can be used for navigation use the list resources tool. Run `playwright-cli --help` for available commands.

## Updating the app host

The user may request that you update the Aspire apphost. You can do this using the `aspire update` command. This will update the apphost to the latest version and some of the Aspire specific packages in referenced projects, however you may need to manually update other packages in the solution to ensure compatibility. You can consider using the `dotnet-outdated` with the users consent. To install the `dotnet-outdated` tool use the following command:

```bash
dotnet tool install --global dotnet-outdated-tool
```

## Deploying with Aspire

### `aspire deploy` vs `azd deploy`

**⚠️ NEVER mix `azd deploy` and `aspire deploy` in the same session.** They manage Azure resources differently and combining them causes volume mount loss, orphaned resources, and broken deployments. Pick one deployment tool per environment and stick with it.

- `aspire deploy` — Aspire-native deployment to Azure Container Apps. Uses the Aspire manifest directly.
- `azd deploy` — Azure Developer CLI deployment. Uses `azure.yaml` + infra templates.

**CRITICAL:** `azd deploy` (or `aspire deploy`) exit code 0 means the **upload** succeeded, NOT that the system works. Always run post-deploy validation — see the e2e-testing skill and `scripts/post-deploy-validate.sh`.

### PostgreSQL on Azure Container Apps

**❌ Containerized Postgres on ACA does NOT work.** Azure Container Apps uses Azure File Shares for persistent volumes, which don't support `chmod`/`chown`. PostgreSQL requires these POSIX operations and will fail to start with permission errors. This is a known platform limitation — see [microsoft/aspire#9631](https://github.com/microsoft/aspire/issues/9631).

**✅ Always use `AddAzurePostgresFlexibleServer` for production deployments:**

```csharp
var dbUser = builder.AddParameter("dbUser");
var dbPassword = builder.AddParameter("dbPassword", secret: true);

var postgresServer = builder.AddAzurePostgresFlexibleServer("db")
    .WithPasswordAuthentication(dbUser, dbPassword)   // REQUIRED — see below
    .RunAsContainer(c => c                             // Local dev uses Docker
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume());

var postgres = postgresServer.AddDatabase("sentencestudio");
```

**Key points:**
- **`.WithPasswordAuthentication(dbUser, dbPassword)` is REQUIRED.** Without it, Aspire defaults to Entra-only authentication, which causes SCRAM authentication errors from EF Core / Npgsql.
- **`.RunAsContainer()`** enables local development with Docker while using the managed Azure Postgres Flexible Server in production. This is the recommended dual-mode pattern.
- **`ContainerLifetime.Persistent`** keeps the local dev container alive across `aspire run` restarts so you don't lose local data between sessions.

### Known `aspire deploy` Issues

- **CLI 13.3.0-preview.1 timeout bug:** `aspire run --detach` can timeout due to a backchannel socket hash mismatch. If you see timeout errors on detach, try running without `--detach` or update the CLI.
- **ACA volume limitation (microsoft/aspire#9631):** Containers that need POSIX filesystem operations (PostgreSQL, MongoDB) cannot use ACA's Azure File Share volumes. Use managed Azure services instead.

## Persistent containers

IMPORTANT! Consider avoiding persistent containers early during development to avoid creating state management issues when restarting the app.

## Aspire workload

IMPORTANT! The aspire workload is obsolete. You should never attempt to install or use the Aspire workload.

## Aspire Documentation Tools

IMPORTANT! The Aspire MCP server provides tools to search and retrieve official Aspire documentation directly. Use these tools to find accurate, up-to-date information about Aspire features, APIs, and integrations:

1. **list_docs**: Lists all available documentation pages from aspire.dev. Returns titles, slugs, and summaries. Use this to discover available topics.

2. **search_docs**: Searches the documentation using keywords. Returns ranked results with titles, slugs, and matched content. Use this when looking for specific features, APIs, or concepts.

3. **get_doc**: Retrieves the full content of a documentation page by its slug. After using `list_docs` or `search_docs` to find a relevant page, pass the slug to `get_doc` to retrieve the complete documentation.

### Recommended workflow for documentation

1. Use `search_docs` with relevant keywords to find documentation about a topic
2. Review the search results - each result includes a **Slug** that identifies the page
3. Use `get_doc` with the slug to retrieve the full documentation content
4. Optionally use the `section` parameter with `get_doc` to retrieve only a specific section

## Official documentation

IMPORTANT! Always prefer official documentation when available. The following sites contain the official documentation for Aspire and related components

1. https://aspire.dev
2. https://learn.microsoft.com/dotnet/aspire
3. https://nuget.org (for specific integration package details)