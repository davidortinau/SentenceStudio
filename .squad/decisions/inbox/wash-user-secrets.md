# Decision: Set up .NET user-secrets workflow

**Author:** Wash (Backend Dev)
**Issue:** #39
**Date:** 2025-07-17
**Branch:** feature/39-user-secrets

## Summary

Established the .NET user-secrets pattern for secure local development across all server-side projects.

## What Changed

1. **Initialized user-secrets** for `SentenceStudio.Api` and `SentenceStudio.WebApp` via `dotnet user-secrets init`. Workers and AppHost already had UserSecretsId configured.

2. **Created `secrets.template.json`** at repo root documenting every secret key, organized by project context (AppHost/Aspire, Api standalone, WebApp standalone, MAUI apps).

3. **Updated `README.md`** section 3 ("API Keys and Secrets") with three clear paths:
   - Option A: Aspire (recommended) -- set secrets once in AppHost, they flow to all services via `WithEnvironment()`
   - Option B: Standalone projects -- set secrets per-project with `dotnet user-secrets`
   - Option C: MAUI mobile/desktop -- use gitignored `appsettings.json` in AppLib

## How Secrets Flow

The AppHost uses Aspire Parameters (`builder.AddParameter("openaikey", secret: true)`) which resolve from the AppHost's user-secrets under `Parameters:openaikey`. These are then passed to child projects via `.WithEnvironment("AI__OpenAI__ApiKey", openaikey)`. Aspire normalizes `__` to `:` in configuration, so `AI__OpenAI__ApiKey` becomes `AI:OpenAI:ApiKey` at the receiving end.

## Projects with UserSecretsId

| Project | UserSecretsId |
|---------|---------------|
| AppHost | d8521a4e-969b-4696-9990-45dea324bda8 |
| Api | 9ae3953f-a490-41b3-a2b8-a8e2555b4615 |
| WebApp | 33f95f89-d495-4311-b6cb-53a47b5c34e6 |
| Workers | dotnet-SentenceStudio.Workers-8ded0183-d135-40b2-b2d4-b49b096922b8 |

## Secrets Inventory

| Secret | AppHost Parameter | Api Key | WebApp Key |
|--------|-------------------|---------|------------|
| OpenAI | Parameters:openaikey | AI:OpenAI:ApiKey | Settings:OpenAIKey |
| ElevenLabs | Parameters:elevenlabskey | ElevenLabsKey | Settings:ElevenLabsKey |
| Syncfusion | Parameters:syncfusionkey | N/A | N/A |

## No Data Impact

No database changes. No existing secrets were moved or deleted. The AppHost's existing user-secrets remain intact and functional.
