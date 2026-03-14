# Decision: Auth Integration Test Infrastructure (#47)

**Date:** 2026-03-13
**Author:** Jayne (Tester)
**Status:** IMPLEMENTED
**Branch:** `feature/47-auth-tests`

## Context

Issue #47 required end-to-end integration tests for the API's authentication flows. The API currently uses DevAuthHandler (auto-authenticates all requests) with config-level support for an Entra ID switch (`Auth:UseEntraId`) that isn't yet wired in code.

## Decision

Created `tests/SentenceStudio.Api.Tests/` — a new test project separate from the existing IntegrationTests (which targets the MAUI app, not the API).

### Test Infrastructure

- **TestJwtGenerator** — generates JWT tokens signed with an HMAC-SHA256 test key, including Entra ID claims (tid, oid, scp, name, email). Supports expired and custom tokens.
- **JwtBearerApiFactory** — `WebApplicationFactory<Program>` that overrides auth to use JWT Bearer validation with the test signing key. Simulates `Auth:UseEntraId=true`.
- **DevAuthApiFactory** — `WebApplicationFactory<Program>` using the default DevAuthHandler. Simulates `Auth:UseEntraId=false`.

### Test Coverage (11 tests, all passing)

**JWT Bearer mode (7 tests):**
- Unauthenticated GET → 401
- Unauthenticated POST → 401
- Valid token → 200
- Expired token → 401
- Garbage token → 401
- Tenant context extraction from JWT claims
- Plans endpoint with valid token

**DevAuthHandler mode (4 tests):**
- Auto-authenticate without token → 200
- Dev claims correctly populated
- All protected endpoints accessible
- TenantContextMiddleware populates context

### Key Constraints

- All tests run without Azure/Entra ID credentials (CI-compatible)
- Added `public partial class Program { }` to API's Program.cs for WebApplicationFactory access
- No scope-based authorization policy tests yet — the API doesn't define any scope policies

## Impact

- Establishes test infrastructure reusable by all future API tests
- When Wash's conditional auth switch (#43) lands, these tests will validate it works correctly
- Tests run in ~0.8s — no external dependencies, no database setup required

## Team Notes

- The existing `tests/SentenceStudio.IntegrationTests/` project references the MAUI app, not the API — keep them separate
- Future scope-based policy tests should be added when policies are implemented
