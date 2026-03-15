# Auth E2E Testing Skill

**Executed by:** Jayne (Tester)  
**Scope:** Complete authentication flow on iOS (dev tunnel, local Aspire, simulator)  
**Duration:** ~2.5 hours (all test cases)  
**Prerequisites:** Aspire running locally, iOS simulator booted, dev tunnel active, webapp running

---

## Test Infrastructure Setup

### 1.1 Verify Test Environment

**Goal:** Ensure all test prerequisites are met.

**Steps:**
1. Confirm Aspire dashboard is accessible at `http://localhost:8080` (or configured port)
2. Verify iOS simulator is booted and responsive:
   ```bash
   xcrun simctl list devices | grep -i "iphone"
   ```
3. Confirm dev tunnel is live (visible in Aspire dashboard or terminal)
4. Verify API URL in mobile app config points to dev tunnel: `https://c60qm31n-7012.use.devtunnels.ms`
5. Confirm webapp is running (Blazor Server)

**Expected Outcome:**
- Aspire dashboard shows all services (Api, WebApp, Workers, Redis, SqliteDb) in running state
- Simulator shows iOS home screen (no crashes)
- API ping succeeds: `curl -s https://c60qm31n-7012.use.devtunnels.ms/health | jq .`

**Verification:**
```bash
# Check API health
curl -s https://c60qm31n-7012.use.devtunnels.ms/health

# Check Aspire dashboard
curl -s http://localhost:8080 -w "\nStatus: %{http_code}\n"
```

---

## Test Suite 1: Registration Flow

### Test Case 1.1: New User Registration (Happy Path)

**Goal:** Verify new user can register with valid email, password, and name.

**Precondition:**
- No existing account with email `testuser-e2e-001@localhost.test`
- Mobile app is at login screen (or onboarding)

**Steps:**

1. **On iOS simulator:**
   - Tap "Register" or "Sign Up" button
   - Fill in:
     - Email: `testuser-e2e-001@localhost.test`
     - Password: `Test@12345`
     - Display Name: `E2E Test User`
   - Tap "Register"
   
2. **Verify response:**
   - Modal/toast appears: "Registration successful" or similar
   - User is either:
     - Auto-logged in (app goes to Onboarding screen), or
     - Redirected to Login screen (if email confirmation required)
   
3. **Check backend:**
   ```bash
   # Query user in database
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Id, Email, DisplayName, EmailConfirmed FROM AspNetUsers WHERE Email = 'testuser-e2e-001@localhost.test';"
   ```
   
   Expected output: 1 row with EmailConfirmed = 1 (in dev mode, auto-confirmed)
   
4. **Check Aspire logs:**
   - Search for "Register" or "User created" in structured logs
   - Verify no auth errors in Api service logs

**Expected Outcome:**
- User registered in database with correct email and display name
- Email is auto-confirmed (dev environment)
- User auto-logs in and proceeds to Onboarding screen

**Screenshots:**
- Registration form filled
- Success confirmation
- Onboarding screen or Dashboard visible

---

### Test Case 1.2: Registration Validation — Duplicate Email

**Goal:** Verify app rejects duplicate email registration.

**Precondition:**
- User `testuser-e2e-001@localhost.test` already exists from Test 1.1
- Mobile app at login/registration screen

**Steps:**

1. **On iOS simulator:**
   - Tap "Register"
   - Fill in:
     - Email: `testuser-e2e-001@localhost.test` (duplicate)
     - Password: `Test@54321`
     - Display Name: `Another User`
   - Tap "Register"

2. **Verify error handling:**
   - Error toast/alert appears: "Email already in use" or "User already exists"
   - App remains on registration screen
   - User can correct input and retry

3. **Check logs:**
   ```bash
   # Aspire logs should show validation error
   # Api service logs should contain something like:
   # "Registration failed: Email already exists"
   ```

**Expected Outcome:**
- Registration rejected with clear error message
- Database shows only original user (no duplicate created)
- User can retry with different email

---

### Test Case 1.3: Registration Validation — Weak Password

**Goal:** Verify password complexity validation works.

**Precondition:**
- Mobile app at registration screen

**Steps:**

1. **On iOS simulator:**
   - Tap "Register"
   - Fill in:
     - Email: `testuser-weak-pwd@localhost.test`
     - Password: `test` (too simple: no uppercase, no number, no special char)
     - Display Name: `Weak Password User`
   - Tap "Register"

2. **Verify validation:**
   - Error message appears (inline or toast): "Password does not meet complexity requirements"
   - Registration blocked

3. **Try again with strong password:**
   - Enter Password: `Test@12345`
   - Registration should succeed

**Expected Outcome:**
- Weak password rejected
- Clear feedback on password requirements
- Strong password accepted
- User created in database

---

## Test Suite 2: Login Flow

### Test Case 2.1: Existing User Login (Happy Path)

**Goal:** Verify existing user can log in with valid credentials.

**Precondition:**
- User `testuser-e2e-001@localhost.test` exists with password `Test@12345` (from Test 1.1)
- Mobile app at login screen (fresh install or after logout)

**Steps:**

1. **On iOS simulator:**
   - Tap email field, enter: `testuser-e2e-001@localhost.test`
   - Tap password field, enter: `Test@12345`
   - Tap "Login"

2. **Verify token flow:**
   - App shows brief loading indicator
   - User is redirected to Onboarding screen (first login) or Dashboard (returning user)
   - No errors in console

3. **Verify secure storage:**
   ```bash
   # On simulator, check if tokens are stored in SecureStorage
   # (This is implicit if user stays logged in after kill/relaunch)
   ```

4. **Check API logs:**
   - Aspire logs should show successful login and token generation
   - No auth errors

**Expected Outcome:**
- User logged in successfully
- JWT token issued and stored
- Refresh token stored in secure storage
- User proceeds to next screen (Onboarding or Dashboard)

**Screenshots:**
- Login screen with credentials filled
- Loading state
- Dashboard or Onboarding screen

---

### Test Case 2.2: Login Validation — Wrong Password

**Goal:** Verify login rejected with incorrect password.

**Precondition:**
- User `testuser-e2e-001@localhost.test` exists
- Mobile app at login screen

**Steps:**

1. **On iOS simulator:**
   - Enter Email: `testuser-e2e-001@localhost.test`
   - Enter Password: `WrongPassword123`
   - Tap "Login"

2. **Verify error handling:**
   - Error toast/alert: "Invalid email or password"
   - App remains on login screen
   - No tokens stored
   - User can retry

**Expected Outcome:**
- Login rejected with generic error (no user enumeration)
- No tokens in secure storage
- User can try again

---

### Test Case 2.3: Login Validation — Non-existent Email

**Goal:** Verify login rejected for non-existent email.

**Precondition:**
- Mobile app at login screen

**Steps:**

1. **On iOS simulator:**
   - Enter Email: `nonexistent@localhost.test`
   - Enter Password: `Test@12345`
   - Tap "Login"

2. **Verify error:**
   - Error: "Invalid email or password" (same generic message as wrong password)
   - No tokens stored

**Expected Outcome:**
- Login rejected
- Generic error prevents user enumeration
- No tokens in secure storage

---

## Test Suite 3: Onboarding Flow

### Test Case 3.1: First-Time Onboarding (After Registration/First Login)

**Goal:** Verify new user is directed to onboarding and can complete language setup.

**Precondition:**
- Fresh user just registered (Test 1.1) or just logged in for first time (Test 2.1)
- User is on Onboarding screen

**Steps:**

1. **On iOS simulator, verify onboarding screen shows:**
   - Native Language selector (e.g., English)
   - Target Language selector (e.g., Korean)
   - Proficiency selector (e.g., Beginner)
   - "Continue" or "Start Learning" button

2. **Fill in onboarding:**
   - Select Native Language: English
   - Select Target Language: Korean
   - Select Proficiency: Beginner
   - Tap "Continue"

3. **Verify starter content creation:**
   - Brief loading indicator
   - App transitions to Dashboard
   - Dashboard shows content (vocabulary, sentences, or activities)

4. **Verify database state:**
   ```bash
   # Check UserProfile was updated with languages
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Id, Email, NativeLanguage, TargetLanguage FROM UserProfiles WHERE Email = 'testuser-e2e-001@localhost.test';"
   ```
   
   Expected: 1 row with NativeLanguage = 'English', TargetLanguage = 'Korean'

5. **Check logs for starter content creation:**
   - Aspire logs should show content generation steps
   - No errors

**Expected Outcome:**
- User profile updated with language preferences
- Starter content created
- User proceeds to Dashboard
- Can view initial content (vocabulary, sentences)

**Screenshots:**
- Onboarding form
- Loading state
- Dashboard with content

---

### Test Case 3.2: Returning User Skips Onboarding

**Goal:** Verify returning user who already completed onboarding skips to Dashboard.

**Precondition:**
- User completed onboarding in Test 3.1
- Kill and relaunch app (or log out and log back in with same user)

**Steps:**

1. **On iOS simulator:**
   - Kill app (long press home, swipe up)
   - Relaunch app from home screen

2. **Verify app state:**
   - App restores session from SecureStorage tokens
   - User goes directly to Dashboard (no login prompt, no onboarding)
   - Content is visible

3. **Verify no network calls for auth:**
   - Check Aspire logs
   - Should see no /api/auth/login call if token is still valid

**Expected Outcome:**
- User auto-logged in via stored token
- Onboarding skipped
- Dashboard visible immediately

**Screenshots:**
- App launch
- Dashboard (no login or onboarding screens)

---

## Test Suite 4: Token Persistence & Security

### Test Case 4.1: Token Stored in SecureStorage (Kill & Relaunch)

**Goal:** Verify JWT token persists in platform-native secure storage across app kills.

**Precondition:**
- User logged in (from Test 2.1 or after onboarding)
- Dashboard is visible
- Session is fresh (token not expired)

**Steps:**

1. **On iOS simulator:**
   - Note the currently logged-in user email in UI
   - Kill app (long press home, swipe up)
   - Wait 2 seconds
   - Relaunch app by tapping app icon

2. **Verify session restore:**
   - App boots and goes directly to Dashboard
   - No login screen
   - User email visible in profile (same user)
   - UI is responsive

3. **Verify in code/logs:**
   - Aspire logs should show no /api/auth/login call
   - IdentityAuthService.SignInAsync() (silent mode) should succeed
   - Token was loaded from SecureStorage

**Expected Outcome:**
- User token persists in secure storage
- App auto-logs in on relaunch
- No user visible interruption

**Screenshots:**
- Dashboard before kill
- Dashboard after relaunch (same user visible)

---

### Test Case 4.2: Token Cleared on Logout

**Goal:** Verify tokens are removed from secure storage when user logs out.

**Precondition:**
- User logged in (Dashboard visible)

**Steps:**

1. **On iOS simulator:**
   - Navigate to Profile or Settings (where logout button is)
   - Tap "Logout" or "Sign Out"
   - Confirm logout if prompted

2. **Verify logout behavior:**
   - App redirects to login screen
   - User email field is empty
   - No cached user info visible

3. **Kill and relaunch app:**
   - App boots to login screen (not Dashboard)
   - SignInAsync() (silent mode) returns null (no stored token)

4. **Verify logs:**
   - IdentityAuthService.SignOutAsync() clears JwtKey, RefreshKey, ExpiresKey
   - No tokens in SecureStorage

**Expected Outcome:**
- Tokens cleared from secure storage on logout
- App boots to login screen on next launch
- Cannot access Dashboard without re-authenticating

**Screenshots:**
- Dashboard with logout button visible
- Login screen after logout
- Login screen after kill/relaunch

---

## Test Suite 5: Token Refresh & Expiry

### Test Case 5.1: Token Auto-Refresh (Before Expiry)

**Goal:** Verify expired access token is refreshed using refresh token without user intervention.

**Precondition:**
- User logged in with valid tokens (from Test 2.1)
- JWT access token is valid for ~15 minutes (default)

**Steps:**

1. **On iOS simulator:**
   - Dashboard is visible
   - Make an API call (e.g., navigate to Activity, fetch vocabulary list)

2. **Monitor token state:**
   - First API call should use cached token (no refresh call)
   - In IdentityAuthService: `_cachedToken` is valid if `_cachedExpires > DateTimeOffset.UtcNow + 60s`

3. **Simulate token near-expiry (for testing):**
   - This is hard to test without mocking time; skip for manual E2E unless mock server available

4. **Verify refresh flow works:**
   - If cached token is expired (but refresh token valid):
     - GetAccessTokenAsync() calls RefreshTokenAsync()
     - POST /api/auth/refresh with refresh token
     - New JWT returned
     - New tokens stored in SecureStorage
     - API call succeeds with new token

5. **Check logs:**
   ```bash
   # Aspire logs should show refresh token endpoint hit
   # Logs should contain: "Token refresh returned 200" or similar
   ```

**Expected Outcome:**
- Token refresh works transparently
- No user-visible interruption
- API calls succeed across token boundary
- New tokens stored securely

**Note:** Full testing requires clock manipulation or mock time; manual spot-check via logs sufficient for E2E.

---

### Test Case 5.2: Refresh Token Expiry (7-day Boundary)

**Goal:** Verify app handles expired refresh token (e.g., 8 days after login).

**Precondition:**
- User logged in (but this test requires simulated time passage; practical approach: check token expiry in database)

**Steps:**

1. **Verify refresh token lifespan in database:**
   ```bash
   # Query RefreshTokens table
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Token, ExpiresAt, RevokedAt FROM RefreshTokens WHERE UserId IN (SELECT Id FROM AspNetUsers WHERE Email = 'testuser-e2e-001@localhost.test') ORDER BY CreatedAt DESC LIMIT 5;"
   ```
   
   Expected: Most recent token has ExpiresAt ~7 days from CreatedAt

2. **Simulate refresh token expiry (via database):**
   - If you have test fixture control, update ExpiresAt to past date:
     ```sql
     UPDATE RefreshTokens SET ExpiresAt = datetime('now', '-1 day') 
     WHERE Token = '<expired-token>';
     ```

3. **On iOS simulator:**
   - Kill and relaunch app (or wait until silent sign-in is attempted)
   - App should redirect to login screen (refresh token expired)
   - Display error or simply redirect with no error

**Expected Outcome:**
- Expired refresh token is rejected
- User forced to re-authenticate
- App goes to login screen
- Clear path to log back in

---

## Test Suite 6: Logout Flow

### Test Case 6.1: User Logout (UI)

**Goal:** Verify user can log out via UI and session is cleared.

**Precondition:**
- User logged in (Dashboard visible)

**Steps:**

1. **On iOS simulator:**
   - Navigate to Profile or Settings screen
   - Find "Logout" or "Sign Out" button
   - Tap it

2. **Verify logout behavior:**
   - App may show confirmation modal: "Are you sure you want to log out?"
   - Tap "Yes" or "Confirm"
   - App redirects to login screen
   - User email is cleared from UI

3. **Verify token cleanup:**
   - Check logs: IdentityAuthService.SignOutAsync() called
   - Tokens removed from SecureStorage

4. **Kill and relaunch to verify persistence:**
   - App boots to login screen (no auto-login)
   - No tokens in storage

**Expected Outcome:**
- User logged out cleanly
- Tokens cleared
- App boots to login screen on relaunch
- No data loss (user profile still in database)

**Screenshots:**
- Dashboard with logout button
- Logout confirmation modal
- Login screen after logout

---

## Test Suite 7: Profile Operations

### Test Case 7.1: View User Profile (After Login)

**Goal:** Verify user can view their profile with correct name and email.

**Precondition:**
- User logged in (email: `testuser-e2e-001@localhost.test`, display name: `E2E Test User`)
- Dashboard or main navigation visible

**Steps:**

1. **On iOS simulator:**
   - Navigate to Profile screen (e.g., tap avatar or "Profile" in menu)
   - Verify displayed information:
     - Email: `testuser-e2e-001@localhost.test`
     - Display Name: `E2E Test User` (or what was set during registration/onboarding)
     - Native Language: English (from onboarding)
     - Target Language: Korean (from onboarding)

2. **Verify data accuracy:**
   ```bash
   # Check profile in database
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Name, Email, NativeLanguage, TargetLanguage FROM UserProfiles WHERE Email = 'testuser-e2e-001@localhost.test';"
   ```

**Expected Outcome:**
- Profile screen displays correct user info
- Data matches database
- No data leakage from other users

**Screenshots:**
- Profile screen with user info visible

---

### Test Case 7.2: Edit Profile (Name & Email)

**Goal:** Verify user can edit display name and email, changes persist.

**Precondition:**
- User on Profile screen (from Test 7.1)

**Steps:**

1. **On iOS simulator:**
   - Find "Edit" button or tap name field
   - Change Display Name: `E2E Test User Updated`
   - Change Email (optional): `testuser-e2e-001-updated@localhost.test`
   - Tap "Save"

2. **Verify save behavior:**
   - Loading indicator appears
   - Toast/confirmation: "Profile updated successfully"
   - Profile screen refreshes with new data

3. **Verify database persistence:**
   ```bash
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Name, Email FROM UserProfiles WHERE Id IN (SELECT UserProfileId FROM AspNetUsers WHERE Email LIKE 'testuser-e2e-001%') LIMIT 1;"
   ```
   
   Expected: Updated display name is present

4. **Kill and relaunch app:**
   - Log back in (if email changed)
   - Profile shows updated information

**Expected Outcome:**
- Profile edits saved to database
- Changes visible immediately
- Changes persist across sessions
- No data loss

**Screenshots:**
- Profile edit form with changes
- Success toast
- Updated profile display

---

### Test Case 7.3: Delete Account

**Goal:** Verify user can delete their account; data is removed, app logs out.

**Precondition:**
- User on Profile screen (can be a temporary test user created for this test)
- Email: `testuser-delete@localhost.test` (created fresh for this test)

**Steps:**

1. **On iOS simulator:**
   - On Profile screen, find "Delete Account" button
   - Tap it
   - Confirmation modal appears: "Are you sure? This cannot be undone."
   - Tap "Yes, Delete My Account"

2. **Verify deletion behavior:**
   - Loading indicator
   - Toast: "Account deleted successfully"
   - App redirects to login screen
   - User is logged out

3. **Verify database state:**
   ```bash
   # User should be deleted
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Email FROM AspNetUsers WHERE Email = 'testuser-delete@localhost.test';"
   ```
   
   Expected: 0 rows (user deleted)
   
   ```bash
   # Associated profile should be deleted or orphaned
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT Email FROM UserProfiles WHERE Email = 'testuser-delete@localhost.test';"
   ```

4. **Try to log back in:**
   - Enter deleted email and any password
   - Login fails: "Invalid email or password"

**Expected Outcome:**
- User account deleted from database
- Associated profile deleted
- App logs out user
- Deleted email cannot log back in
- No orphaned data

**Screenshots:**
- Profile with delete button
- Confirmation modal
- Login screen after deletion
- (Backend verification of deletion)

---

## Test Suite 8: Data Isolation (User A vs User B)

### Test Case 8.1: Data Isolation — Vocabulary/Content Not Shared

**Goal:** Verify User A's vocabulary and content is not visible to User B.

**Precondition:**
- Create two users:
  - User A: `testuser-a@localhost.test` (password: `TestA@12345`)
  - User B: `testuser-b@localhost.test` (password: `TestB@12345`)
- Both completed onboarding with different target languages (User A: Korean, User B: Spanish)

**Steps:**

1. **Login as User A:**
   - On iOS, log in as `testuser-a@localhost.test`
   - Go through onboarding (or skip if already done)
   - Go to Vocabulary screen
   - Note the vocabulary list (e.g., 10 items for Korean)

2. **Logout and login as User B:**
   - Logout from profile
   - Log in as `testuser-b@localhost.test`
   - Go to Vocabulary screen
   - Verify vocabulary list is different (Spanish content, different count)

3. **Verify in database:**
   ```bash
   # Get User A's content
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT COUNT(*) as UserAVocab FROM Vocabularies WHERE UserProfileId IN (SELECT Id FROM UserProfiles WHERE Email = 'testuser-a@localhost.test');"
   
   # Get User B's content
   sqlite3 ~/.sentencestudio/sentencestudio.db \
     "SELECT COUNT(*) as UserBVocab FROM Vocabularies WHERE UserProfileId IN (SELECT Id FROM UserProfiles WHERE Email = 'testuser-b@localhost.test');"
   ```
   
   Expected: Different counts or no overlap

4. **Check API logs:**
   - Ensure API calls are filtering by user (tenant isolation)
   - Aspire logs should show TenantContext.CurrentUserId being set correctly per request

**Expected Outcome:**
- Each user sees only their own content
- No data leakage between users
- Database queries correctly filter by user

**Screenshots:**
- User A's vocabulary list
- User B's different vocabulary list
- Profile info for each user (different email, language)

---

### Test Case 8.2: JWT Claim Verification (UserId in Token)

**Goal:** Verify JWT token contains correct user ID and app uses it for API calls.

**Precondition:**
- User logged in as `testuser-a@localhost.test`

**Steps:**

1. **Decode JWT token (manual verification):**
   - Get the JWT from SecureStorage (can be logged in debug mode, or extract from API call)
   - Use an online JWT decoder (https://jwt.io/) or decode in code:
     ```csharp
     var handler = new JwtSecurityTokenHandler();
     var token = handler.ReadJwtToken(accessToken);
     var userIdClaim = token.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);
     ```

2. **Verify claims in token:**
   - `sub` (subject/user ID) matches user's ApplicationUser.Id
   - `email` claim matches email
   - `name` claim matches display name
   - `exp` (expiration) is reasonable (15 min from now)

3. **Check API calls in Aspire logs:**
   - Aspire logs should show extracted user ID from token
   - TenantContext.CurrentUserId should match token's user ID

**Expected Outcome:**
- JWT contains correct user ID and claims
- User ID in token matches database user
- API uses token claims to enforce data isolation

**Note:** This is a backend verification test; inspect logs and token structure.

---

## Test Suite 9: Error Handling & Network Resilience

### Test Case 9.1: API Server Down (Connection Refused)

**Goal:** Verify app handles unreachable API gracefully.

**Precondition:**
- User at login screen
- Dev tunnel or API server is accessible (normal state)

**Steps:**

1. **Disable dev tunnel or kill API server:**
   - Stop Aspire AppHost (Ctrl+C or via dashboard)
   - Or disconnect dev tunnel
   - Verify API is unreachable:
     ```bash
     curl -s https://c60qm31n-7012.use.devtunnels.ms/health -w "\nStatus: %{http_code}\n"
     # Should fail or timeout
     ```

2. **On iOS simulator:**
   - Try to log in (email and password filled)
   - Tap "Login"

3. **Verify error handling:**
   - Brief loading indicator
   - Toast/alert: "Unable to connect to server" or "Connection failed"
   - User remains on login screen (not stuck)
   - Can retry or close alert

4. **Bring API back online:**
   - Restart Aspire AppHost
   - Wait for health check to pass
   - On iOS, user can tap "Login" again
   - Should succeed this time

**Expected Outcome:**
- Graceful error message (not crash)
- User can retry
- App recovers when API comes back

**Screenshots:**
- Error alert/toast
- Login screen still usable

---

### Test Case 9.2: Network Timeout (Slow Connection)

**Goal:** Verify app handles slow/timeout API response gracefully.

**Precondition:**
- API is reachable but slow

**Steps:**

1. **Simulate slow API (using network throttling or server delay):**
   - On iOS simulator, go to Settings > Developer > Network Link Conditioner
   - Or via Xcode: Simulate slow network
   - Or add a delay to API auth endpoint (for testing)

2. **On iOS simulator:**
   - Try to log in
   - Observe loading indicator
   - After 10-30 seconds, timeout occurs

3. **Verify timeout handling:**
   - Loading indicator is dismissed
   - Error toast: "Request timed out" or "Please try again"
   - User on login screen, can retry

4. **Restore normal network:**
   - Disable network throttling
   - Login should succeed on retry

**Expected Outcome:**
- Timeout is handled (not infinite loading)
- Clear error message
- User can retry

---

### Test Case 9.3: Invalid/Malformed API Response

**Goal:** Verify app handles unexpected API response gracefully.

**Precondition:**
- Mock API or add conditional error response for testing

**Steps:**

1. **Simulate malformed response (backend test):**
   - Temporarily modify API to return invalid JSON or wrong schema
   - E.g., return `{ "error": "..." }` instead of expected AuthResponse shape

2. **On iOS simulator:**
   - Try to log in
   - Observe loading indicator

3. **Verify error handling:**
   - App detects deserialization failure
   - Toast: "An error occurred" or similar generic message
   - No crash
   - User on login screen

4. **Fix API response:**
   - Restore correct response format
   - Login should work on retry

**Expected Outcome:**
- Graceful handling of malformed responses
- No app crash
- User can retry

**Note:** This test is optional; requires controlled backend mutation.

---

## Test Suite 10: Webapp Regression Testing

### Test Case 10.1: Webapp Login Still Works

**Goal:** Verify webapp (Blazor Server) auth still works after mobile auth changes.

**Precondition:**
- Webapp is running at `https://localhost:7200` (or configured URL)
- User `testuser-e2e-001@localhost.test` exists with password `Test@12345`

**Steps:**

1. **Open browser (Playwright):**
   ```bash
   # Use Playwright or manual browser testing
   # Navigate to https://localhost:7200
   ```

2. **On webapp:**
   - Login screen visible (with email/password fields)
   - Enter email: `testuser-e2e-001@localhost.test`
   - Enter password: `Test@12345`
   - Click "Login"

3. **Verify login behavior:**
   - Brief loading indicator
   - Redirected to onboarding or dashboard
   - User info visible in header (email or display name)

4. **Verify session:**
   - Refresh page
   - Still logged in (session persists via Redis distributed cache)
   - No redirect to login

5. **Check logs:**
   - Aspire logs show successful login
   - No API auth errors

**Expected Outcome:**
- Webapp login works
- Session persists across requests
- Token acquired and cached (Redis)

**Playwright Script:**
```javascript
// e2e-testing-workspace/tests/auth-webapp.spec.ts
import { test, expect } from '@playwright/test';

test('webapp login flow', async ({ page }) => {
  await page.goto('https://localhost:7200');
  
  await page.fill('input[name="email"]', 'testuser-e2e-001@localhost.test');
  await page.fill('input[name="password"]', 'Test@12345');
  await page.click('button:has-text("Login")');
  
  await page.waitForNavigation();
  await expect(page).toHaveURL(/\/(dashboard|onboarding)/);
  
  // Verify user info visible
  await expect(page.locator('text=E2E Test User')).toBeVisible();
});
```

---

### Test Case 10.2: Webapp Logout Clears Session

**Goal:** Verify webapp logout clears session and redirects to login.

**Precondition:**
- Logged into webapp (from Test 10.1)

**Steps:**

1. **On webapp:**
   - Navigate to profile or find logout button
   - Click "Logout" or "Sign Out"

2. **Verify logout:**
   - Redirected to login screen
   - No cached session (can't navigate back to protected page)

3. **Try to access dashboard directly:**
   - Navigate to `/dashboard` URL directly
   - Redirected to login screen

**Expected Outcome:**
- Webapp logout works
- Session cleared
- Redirects to login on next access

---

### Test Case 10.3: Webapp Registration Still Works

**Goal:** Verify webapp registration endpoint works alongside mobile auth.

**Precondition:**
- Webapp is running

**Steps:**

1. **On webapp:**
   - Navigate to registration page
   - Fill in:
     - Email: `testuser-webapp@localhost.test`
     - Password: `TestWebApp@12345`
     - Display Name: `Webapp Test User`
   - Click "Register"

2. **Verify registration:**
   - Success message or auto-login
   - User created in database
   - Can log in with new credentials

3. **Verify on mobile:**
   - On iOS simulator, try to log in as the same user
   - Should succeed (same backend)

**Expected Outcome:**
- Webapp registration works
- User accessible from mobile too (shared database)
- Cross-platform compatibility

---

## Test Suite 11: Load & Stress (Optional, Advanced)

### Test Case 11.1: Concurrent Login Attempts

**Goal:** Verify system handles multiple simultaneous login attempts.

**Precondition:**
- Use load testing tool (JMeter, k6) or mobile device farm

**Steps:**

1. **Simulate 10 concurrent logins:**
   - Same user on 10 simulator instances
   - Or: 10 different users logging in simultaneously

2. **Verify system stability:**
   - All logins succeed within 5 seconds
   - Database locks don't occur
   - All users get unique tokens
   - No 500 errors in API logs

3. **Check API performance:**
   - Aspire logs for latency
   - No auth bottlenecks

**Expected Outcome:**
- System handles concurrent logins
- Response time remains acceptable (<3s per login)
- No data corruption

**Note:** Optional for manual E2E; recommended for performance testing phase.

---

## Summary Checklist

**Registration:**
- [ ] Test 1.1: New user registration (happy path)
- [ ] Test 1.2: Duplicate email validation
- [ ] Test 1.3: Password complexity validation

**Login:**
- [ ] Test 2.1: Existing user login (happy path)
- [ ] Test 2.2: Wrong password rejection
- [ ] Test 2.3: Non-existent email handling

**Onboarding:**
- [ ] Test 3.1: First-time onboarding flow
- [ ] Test 3.2: Returning user skips onboarding

**Token Persistence:**
- [ ] Test 4.1: Token stored in SecureStorage
- [ ] Test 4.2: Token cleared on logout

**Token Management:**
- [ ] Test 5.1: Token auto-refresh (spot-check)
- [ ] Test 5.2: Refresh token expiry handling

**Logout:**
- [ ] Test 6.1: User logout clears tokens

**Profile:**
- [ ] Test 7.1: View profile
- [ ] Test 7.2: Edit profile
- [ ] Test 7.3: Delete account

**Data Isolation:**
- [ ] Test 8.1: User A's data not visible to User B
- [ ] Test 8.2: JWT claims verification

**Error Handling:**
- [ ] Test 9.1: API server down handling
- [ ] Test 9.2: Network timeout handling
- [ ] Test 9.3: Malformed response handling

**Webapp Regression:**
- [ ] Test 10.1: Webapp login still works
- [ ] Test 10.2: Webapp logout clears session
- [ ] Test 10.3: Webapp registration works

**Load Testing (Optional):**
- [ ] Test 11.1: Concurrent login attempts

---

## Tools & Shortcuts

**Simulator Management:**
```bash
# Boot iOS simulator
xcrun simctl boot "iPhone 17 Pro"

# Shutdown
xcrun simctl shutdown "iPhone 17 Pro"

# View console logs
xcrun simctl spawn booted log stream --level debug --predicate 'process == "SentenceStudio"'
```

**Database Queries (Quick Reference):**
```bash
# Open database
sqlite3 ~/.sentencestudio/sentencestudio.db

# List tables
.tables

# User count
SELECT COUNT(*) FROM AspNetUsers;

# Tokens by user
SELECT Email, COUNT(*) as TokenCount FROM AspNetUsers 
LEFT JOIN RefreshTokens ON AspNetUsers.Id = RefreshTokens.UserId 
WHERE RefreshTokens.RevokedAt IS NULL 
GROUP BY AspNetUsers.Id;

# Recent login attempts (from logs)
# (Check Aspire dashboard structured logs for "LoginRequest" events)
```

**Aspire Logs (Structured):**
```bash
# Query from Aspire dashboard
# https://localhost:8080 → Structured Logs
# Filter by resource: "Api"
# Search: "Login" or "Register" or "auth"

# Or via API (if available):
curl -s http://localhost:8080/api/resources/Api/logs?filter=Login | jq .
```

**Screenshots (Playwright/maui-devflow):**
```bash
# On iOS simulator (via maui-devflow or Appium)
# Tap screenshot button or programmatic capture in test
```

---

## Known Issues & Workarounds

1. **Token Expiry Testing:**
   - Hard to test without mocking system time
   - Workaround: Check token expiry in database or decode JWT
   - Refresh flow can be verified via logs without full expiry

2. **SecureStorage Inspection:**
   - Can't directly inspect iOS SecureStorage from test harness
   - Workaround: Test indirectly via app behavior (kill/relaunch)

3. **Network Simulation:**
   - Simulator network is same as host machine
   - Use Xcode's network throttling or external tool (Charles Proxy, Fiddler)

4. **Concurrent Testing:**
   - Single simulator can't test concurrent logins
   - Use device farm or multiple simulators (resource-intensive)

---

## Next Steps

1. **Execute tests sequentially** from Test Suite 1 onwards
2. **Document results** in spreadsheet (test case, status, notes, screenshot links)
3. **Report blockers** to Zoe (Lead) immediately if any critical failures
4. **Regression suite** (Test Suite 10) must pass before each mobile/webapp release
5. **Archive logs** from Aspire for failed test cases

---

**Version:** 1.0  
**Last Updated:** 2026-03-14  
**Author:** Zoe (Lead)  
**Maintainer:** Jayne (Tester)
