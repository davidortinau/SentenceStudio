# Decision: GitHub Actions CI Workflow (#56)

**Date:** 2026-03-13  
**Author:** Kaylee (Full-stack Dev)  
**Status:** IMPLEMENTED  

## What

Created `.github/workflows/ci.yml` — a GitHub Actions CI pipeline that builds and tests the SentenceStudio projects on every push to `main` and every PR targeting `main`.

## Build Matrix

| Project | Path | Notes |
|---------|------|-------|
| Api | `src/SentenceStudio.Api` | ASP.NET Core Web API |
| WebApp | `src/SentenceStudio.WebApp` | Blazor web app |
| AppLib | `src/SentenceStudio.AppLib` | MAUI shared library (installs MAUI workload) |

## Test Projects

- `tests/SentenceStudio.UnitTests` — xUnit, targets net10.0
- `tests/SentenceStudio.IntegrationTests` — xUnit, targets net10.0

**Known issue:** IntegrationTests references `SentenceStudio.csproj` which no longer exists. This will surface as a CI failure — a follow-up fix is needed.

## Key Decisions

1. **.NET SDK from global.json** — `actions/setup-dotnet` reads `global.json` directly for version 10.0.101
2. **NuGet caching** — `actions/cache` on `~/.nuget/packages` keyed by csproj/NuGet.config hashes
3. **Local NuGet source removal** — `sed` strips the `localnugets` source (dev-machine-only path) before restore
4. **DevAuthHandler in CI** — `Auth__UseEntraId=false` env var ensures no Entra ID dependency in CI
5. **Test reporting** — TRX results uploaded as artifacts; `dorny/test-reporter` publishes inline PR results
6. **MAUI workload** — Only installed for AppLib matrix entry (conditional on `matrix.maui`)
7. **fail-fast: false** — All matrix entries run even if one fails, for maximum signal

## Branch

`feature/56-ci-workflow` — ready for PR when approved.
