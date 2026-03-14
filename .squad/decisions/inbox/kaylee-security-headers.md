# Security Headers and HTTPS Enforcement (#41)

**Author:** Kaylee (Full-stack Dev)
**Date:** 2025-07-18
**Branch:** `feature/41-security-headers`

## What Changed

Added security hardening across all three web services (API, WebApp, Marketing):

### Security Headers (all services)
Shared extension method `UseSecurityHeaders()` in `src/Shared/SecurityHeadersExtensions.cs`, linked into each web project via `<Compile Include>`:
- `X-Content-Type-Options: nosniff` -- prevents MIME-type sniffing
- `X-Frame-Options: DENY` -- blocks clickjacking via iframes
- `Referrer-Policy: strict-origin-when-cross-origin` -- limits referrer leakage
- `Permissions-Policy: camera=(), microphone=(), geolocation=()` -- restricts browser APIs

### HTTPS and HSTS
- HTTPS redirect now environment-aware: skipped in Development (Aspire terminates TLS at proxy)
- API gets explicit HSTS config: 365-day max-age, includeSubDomains, preload
- WebApp and Marketing already had HSTS in non-dev block (unchanged)

### CORS (API only)
- `AllowWebApp` policy: restricts origins to values from `Cors:AllowedOrigins` config
- `AllowDevClients` policy (dev only): allows any localhost origin with credentials
- Production origins configured in `appsettings.Production.json`
- MAUI clients use service discovery (not browser CORS), so no impact

### AllowedHosts
- Created `appsettings.Production.json` for API, WebApp, and Marketing
- Production restricts to specific domain names instead of wildcard `*`

## Why Linked Source File Instead of WebServiceDefaults

The `SentenceStudio.ServiceDefaults` (MAUI) and `SentenceStudio.WebServiceDefaults` both define `public static class Extensions` in `Microsoft.Extensions.Hosting` with an `AddServiceDefaults()` method. Referencing both from a web project would cause ambiguous call errors. A linked source file avoids that conflict cleanly.

## What is NOT Included
- Production CORS fine-tuning (tracked in #62)
- Content-Security-Policy header (complex, needs per-app tuning for Blazor inline scripts)
- Production authentication (still using DevAuthHandler)
