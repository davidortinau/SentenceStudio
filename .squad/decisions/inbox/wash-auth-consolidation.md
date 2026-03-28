# Auth Route Consolidation + Secure Storage Encryption

**Date:** $(date +%Y-%m-%d)
**Author:** Wash (Backend Dev)
**Status:** IMPLEMENTED

## Auth Routes

The WebApp had duplicate auth pages: server-rendered `/Account/*` forms AND shared Blazor `/auth/*` pages. The shared pages already worked — `ServerAuthService` returns a userId|token pair and redirects to `/account-action/AutoSignIn` to set the cookie.

**Changes:**
- Cookie auth paths now point to `/auth/login` and `/account-action/SignOut`
- `Account/Login.razor`, `Register.razor`, `ForgotPassword.razor` now redirect to `/auth/*` counterparts
- **Kept as-is:** `ResetPassword.razor`, `ConfirmEmail.razor`, `AccessDenied.razor` (they receive tokens from email links)
- Removed `/account-action/Login` POST and `/account-action/Register` POST (dead endpoints — shared pages use AutoSignIn)
- All `/Account/Login` redirects in endpoints updated to `/auth/login`

**Bug fix:** Added `NativeLanguage` to the `is_onboarded` check in AutoSignIn handler, matching Kaylee's earlier fix in the Blazor UI.

## Secure Storage

`WebSecureStorageService` previously stored sensitive values (auth tokens) in plain text JSON. Now uses ASP.NET Core Data Protection API to encrypt/decrypt values before writing to the preferences file. Gracefully handles key rotation — returns null and logs a warning if decryption fails.

**Impact:** All team members — any code that calls `ISecureStorageService` on web now gets encrypted storage. No interface changes.
