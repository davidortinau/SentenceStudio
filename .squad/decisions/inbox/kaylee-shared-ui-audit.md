# Shared UI Audit: WebApp vs Shared UI Page Duplication

**Date:** 2026-03-16  
**Author:** Kaylee (Full-stack Dev)  
**Status:** DOCUMENTED  
**Requested by:** Captain (David Ortinau)

## Summary

Audited all pages in `src/SentenceStudio.WebApp/Components/Pages/` against `src/SentenceStudio.UI/Pages/` to identify consolidation opportunities per the Captain's directive: "shared UI whenever possible, unless there's a strong technical reason they need to be separate."

**Result:** All 6 WebApp Account pages must remain separate. 3 Blazor template leftovers removed.

---

## WebApp Account Pages (6 total)

### MUST Stay Separate: Login.razor, Register.razor

**Route:** `/Account/Login`, `/Account/Register`  
**Technical constraint:** Cookie-based authentication via `<form method="post">` + ASP.NET Identity `SignInManager`.

Blazor Server renders over WebSocket — it **cannot** set HTTP-only auth cookies from an interactive component. The only way to set cookies is a full HTML form POST to a minimal API endpoint (`/account-action/Login`, `/account-action/Register`), which returns an HTTP redirect with `Set-Cookie` headers.

The shared UI versions (`/auth/login`, `/auth/register`) work differently: they call `IAuthService.SignInAsync()` interactively, get a token back, and redirect to `/account-action/AutoSignIn` for cookie-setting. This two-hop flow works but is architecturally different. Both sets coexist — the WebApp pages are the primary entry point for web users.

### SHOULD Stay Separate: ForgotPassword.razor

**Route:** `/Account/ForgotPassword`  
**Technical constraint:** Email link URL generation is server-bound.

Although ForgotPassword doesn't set cookies, it's tightly coupled to the WebApp's password reset flow:

1. The WebApp endpoint (`/account-action/ForgotPassword`) uses `httpContext.Request.Host` to construct the email reset link pointing to `/Account/ResetPassword` on the **WebApp's own domain**
2. The shared UI's `ForgotPasswordPage` calls the API's `/api/auth/forgot-password`, which generates the link using the **API server's domain** — wrong destination for WebApp users
3. Removing the WebApp version would break the email link flow

The shared UI ForgotPasswordPage at `/auth/forgot-password` exists and works for MAUI/API-backed clients. It's not a true duplicate — it serves a different deployment topology.

### MUST Stay Separate: ResetPassword.razor

**Route:** `/Account/ResetPassword`  
**Technical constraint:** No shared UI equivalent exists. Email links point here.

- Password reset emails link to `/Account/ResetPassword?email=...&token=...`
- The page renders a form that POSTs to `/account-action/ResetPassword`
- The endpoint uses `UserManager.ResetPasswordAsync()` server-side
- Creating a shared UI version would require the API to handle reset tokens AND the email links to be updated — a future enhancement, not a quick consolidation

### SHOULD Stay Separate: AccessDenied.razor, ConfirmEmail.razor

**Route:** `/Account/AccessDenied`, `/Account/ConfirmEmail`  
**Technical constraint:** ASP.NET auth infrastructure redirect targets.

- `AccessDenied` — default redirect target when cookie auth denies access. Hardcoded in ASP.NET auth middleware.
- `ConfirmEmail` — placeholder display while `/account-action/ConfirmEmail` processes the server-side token. The actual confirmation is a GET endpoint redirect, not a Blazor page interaction.

Both are tiny (~15 lines each), web-only by nature, and have zero maintenance burden.

---

## Non-Account Pages Cleaned Up

Removed 3 Blazor project template leftovers that served no purpose:

| Page | Route | Reason |
|------|-------|--------|
| `Counter.razor` | `/counter` | Default Blazor template, not a real feature |
| `Weather.razor` | `/weather` | Default Blazor template, not a real feature |
| `Home.razor` | `/home-template` | Template page, shared UI `Index.razor` serves `/` |

`Error.razor` (`/Error`) and `NotFound.razor` (`/not-found`) kept — they use `HttpContext` and are WebApp-specific infrastructure.

---

## Remaining Pages — No Duplication Found

The WebApp's `AppRoutes.razor` correctly references the shared UI assembly as the primary route source:

```razor
<Router AppAssembly="typeof(SentenceStudio.WebUI.Routes).Assembly"
        AdditionalAssemblies="new[] { typeof(AppRoutes).Assembly }">
```

All activity pages (Vocabulary, Skills, Conversation, etc.) exist **only** in shared UI. The WebApp's own pages are limited to auth infrastructure and the error/not-found handlers. No other duplication exists.

---

## Gap: No Shared UI ResetPasswordPage

The shared UI has `ForgotPasswordPage` but **no `ResetPasswordPage`**. If a MAUI user receives a password reset email, the link points to `/Account/ResetPassword` which only exists in the WebApp. 

**Future consideration:** Create a shared UI `ResetPasswordPage` at `/auth/reset-password` that calls the API's `/api/auth/reset-password` endpoint, and update the API's email link URL to be configurable per client type.

---

## Decision

**Keep all 6 WebApp Account pages as a cohesive server-side auth unit.** They form an interconnected workflow (login → register → forgot password → email link → reset password → confirm email → login) that depends on ASP.NET Identity cookie auth and server-side token processing. Partially consolidating (e.g., removing only ForgotPassword) would create an inconsistent split between `/auth/*` and `/Account/*` routes within the same flow.

The Captain's shared-UI-first directive is fully satisfied for all non-auth pages. Auth pages are the legitimate exception documented here.
