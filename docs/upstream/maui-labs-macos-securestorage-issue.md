# Upstream issue draft — macOS AppKit `SecureStorage` can deadlock app startup

**Target repo:** `dotnet/maui-labs`
**File:** `platforms/MacOS/src/MacOS.Essentials/SecureStorageImplementation.cs`
**Status:** draft, not yet filed. No existing issue found in `dotnet/maui`, `dotnet/maui-labs`,
`dotnet/macios`, or `shinyorg/mauiplatforms`.

---

## Title

macOS AppKit `SecureStorage` uses the legacy ACL-gated keychain with a machine-global service
name — reads can block app startup forever behind a SecurityAgent prompt

## Versions (read from this machine, not assumed)

| Component | Version |
|---|---|
| `Microsoft.Maui.Platforms.MacOS` | `0.26.0-dev` |
| `Microsoft.Maui.Platforms.MacOS.Essentials` | `0.26.0-dev` |
| `Microsoft.Maui.Platforms.MacOS.BlazorWebView` | `0.26.0-dev` |
| `Microsoft.Maui.Controls` | `11.0.0-preview.4.26230.3` |
| .NET SDK (selected; no `global.json` present) | `11.0.100-preview.7.26381.103` |
| Workload version | `11.0.100-preview.7.26410.2` (`maui 11.0.0-preview.7.26406.9`, `macos 26.5.11997-net11-p7`) |
| `Microsoft.macOS.Ref` (net11) | `26.5.11997-net11-p7` |
| TFM | `net11.0-macos` |
| Host | macOS on Apple silicon, Xcode 26 SDK |

## Summary

On the macOS AppKit head, `SecureStorage.Default.GetAsync(key)` can **block forever**. It neither
returns nor throws, so no `try/catch`, `Task.Run`, or `ConfigureAwait` on the caller's side can
recover. Any startup path that awaits a token restore deadlocks — in our app, Blazor's
`AuthorizeRouteView` stayed in its `<Authorizing>` state and the window sat on
"Checking authentication…" indefinitely.

## Root cause

`SecureStorageImplementation` reads generic passwords like this:

```csharp
const string ServiceName = "maui_secure_storage";

string? GetValue(string key)
{
    using var record = new SecRecord(SecKind.GenericPassword) { Account = key, Service = ServiceName };
    using var match  = SecKeyChain.QueryAsRecord(record, out var resultCode);   // SecItemCopyMatching
    if (resultCode == SecStatusCode.Success && match?.ValueData != null)
        return NSString.FromData(match.ValueData, NSStringEncoding.UTF8);
    return null;
}
```

Because `kSecUseDataProtectionKeychain` is not set and the AppKit head is not sandboxed, the request
is serviced by the **legacy file-based keychain**:

```
SecItemCopyMatching
  -> SecItemCopyMatching_osx
    -> SecKeychainSearchCopyNext
      -> SecKeychainItemCopyContent
        -> Security::KeychainCore::ItemImpl::getContent
```

Legacy items carry a per-item ACL whose trusted-application list is the **creating binary's code
signature**. Dumping a freshly written item's ACL (`SecKeychainItemCopyAccess` →
`SecAccessCopyACLList` → `SecACLCopyContents` / `SecACLCopyAuthorizations`):

| ACL | trusted apps | authorizations |
|---|---|---|
| 0 | **1** — the creating binary | Decrypt, Derive, ExportClear, ExportWrapped, MAC, Sign |
| 1 | NULL (any app) | Encrypt |
| 2 | NULL (any app) | Integrity |
| 3 | NULL (any app) | PartitionID |
| 4 | empty (all apps) | ChangeACL |

Reading the value requires `Decrypt`, which only the creating signature holds.

**Debug builds of the macOS head are ad-hoc signed** (`codesign -dvvv` → `flags=0x2(adhoc)`,
`TeamIdentifier=not set`), so **every rebuild produces a new cdhash** and is a different application
as far as the keychain is concerned. Observed across one afternoon: `122a26e5…`, `b23971e3…`,
`caac7347…`, `d369083f…`. macOS therefore raises a modal SecurityAgent dialog on the next read, and
`SecItemCopyMatching` blocks until it is answered. On CI, on a locked screen, or in any automated
run, it is never answered.

## Repro

1. A MAUI app targeting `net11.0-macos` with `Microsoft.Maui.Platforms.MacOS.*`.
2. On startup, `await SecureStorage.Default.SetAsync("k", "v");` then run the app once.
3. Rebuild (Debug — the ad-hoc signature changes) and run again.
4. Startup calls `await SecureStorage.Default.GetAsync("k")`.
5. **Expected:** the call completes, or fails with a status the caller can inspect.
   **Actual:** a SecurityAgent prompt appears and the call never returns until it is answered.

## Three distinct defects

### 1. No way to perform a non-interactive read

The legacy keychain's UI is controlled by `SecKeychainSetUserInteractionAllowed(Boolean)`
(`<Security/SecKeychain.h>`, `API_DEPRECATED(macos(10.2, 10.10))`, still exported). With it set to
`false`, the same reads that previously blocked return **immediately**:

```
SecKeychainSetUserInteractionAllowed(false)   -> 0

read auth_refresh (returnData=1) -> -25293 errSecAuthFailed     (10.0 ms, no prompt)
read auth_jwt     (returnData=1) -> -25293 errSecAuthFailed      (1.3 ms, no prompt)
read missing key  (returnData=1) -> -25300 errSecItemNotFound    (1.4 ms)
read auth_refresh (returnData=0) ->      0 errSecSuccess         (0.2 ms)
```

`SecureStorage` gives callers no way to ask for this, and no way to see the `OSStatus`: `GetValue`
collapses every non-success code to `null`.

**Suggested fix:** an opt-in non-interactive read (or make automatic reads non-interactive by
default) and surface the `OSStatus`, so callers can distinguish "not stored" from "needs the user".

### 2. `SetValue` silently does nothing when the item already exists

```csharp
var status = SecKeyChain.Add(record);
if ((int)status > 0)                       // errSecDuplicateItem is -25299, i.e. NOT > 0
    throw new InvalidOperationException(...);
```

`SetValue` first calls `Remove(key)`, which itself calls `QueryAsRecord` — a **data-returning**
query, so it hits the same ACL gate and returns non-success, so the remove never happens. `Add` then
returns `errSecDuplicateItem (-25299)`, which the `> 0` check lets through. The write is silently
lost.

Verified separately that a working, non-prompting replace does exist — legacy generic-password items
have **no** `ACLAuthorizationDelete` entry, so:

```
B SecItemDelete                  -> -25244 errSecInvalidOwnerEdit
B SecKeychainFindGenericPassword -> 0      (attributes only, no ACL data read)
B SecKeychainItemDelete          -> 0      errSecSuccess          <- works, no prompt
B SecItemAdd (retry)             -> 0      errSecSuccess          <- item now owned by B
```

**Suggested fix:** treat any non-`errSecSuccess` status as failure (`!= 0`, not `> 0`), and
implement `SetValue` as add → on duplicate, legacy find + `SecKeychainItemDelete` → add, so the item
ends up owned by the running signature and is readable back.

### 3. The keychain service name is machine-global, not app-scoped

`ServiceName` is the constant `"maui_secure_storage"` for **every** MAUI macOS app on the machine.
`dotnet/maui`'s own iOS/Mac Catalyst implementation does the opposite —
`src/Essentials/src/SecureStorage/SecureStorage.shared.cs` uses
`Preferences.GetPrivatePreferencesSharedName("preferences")`, i.e.
`{PackageName}.microsoft.maui.essentials.preferences`.

Consequences on a real machine: this login keychain contains
`svce="maui_secure_storage", acct="MAUI Sherpa Local Vault"` from an unrelated MAUI app alongside
our app's `auth_jwt` / `auth_refresh` / `auth_expires`. Two MAUI apps that pick the same key name
collide, and `RemoveAll()` — which deletes by `Service` only — would **delete another
application's credentials**.

**Suggested fix:** scope `ServiceName` to the app, matching `dotnet/maui`'s iOS/Mac Catalyst
behaviour.

#### Why this is worse than a collision

The shared name does not merely risk two apps overwriting each other. It removes the only
information an app would need to behave safely, and it does so in a namespace where the destructive
operation is the one that *is* permitted:

* **Reads are ACL-gated, deletes are not.** A foreign item answers `errSecAuthFailed` (-25293) to a
  data read, but `SecKeychainFindGenericPassword` + `SecKeychainItemDelete` removes it and returns
  `errSecSuccess`. Legacy generic-password items carry no `ACLAuthorizationDelete` entry (dumped via
  `SecACLCopyContents`/`SecACLCopyAuthorizations`). So the operation an app is least entitled to
  perform is the one that always works.
* **The account name carries no owner.** `auth_refresh` is `auth_refresh` whoever wrote it. An app
  migrating "its own" pre-existing item, or cleaning up "its own" stale copy, is in fact acting on a
  name — and on this machine that name is shared with an unrelated product's
  `MAUI Sherpa Local Vault` in the same service.

The combination makes the obvious implementations wrong in both directions: a migration that copies
what it finds imports another product's refresh token and presents it to its own backend, and a
cleanup that deletes what it finds destroys another product's credential. Neither app sees an error.

**What this app does instead**, for anyone hitting the same wall before the framework changes:

1. All normal reads and writes use an app-scoped account (`<bundle-id>.<key>`). Nothing in the
   ordinary code path ever names a bare account.
2. The pre-namespacing accounts are treated as foreign until corroborated. Adoption requires a
   complete, self-consistent credential triple *and* an access-token identity claim matching an
   app-private preference that no other application can write.
3. Nothing bare is ever deleted — not on write, not on sign-out, not after a successful adoption.
   Adoption copies and then permanently suppresses further reads via a durable, app-scoped marker.
   Leaving a stale item costs nothing that was not already true; deleting one that cannot be proven
   ours is unrecoverable for its owner.

## Note on the modern keychain

`kSecUseDataProtectionKeychain` is not a drop-in workaround for Debug builds: an ad-hoc signed,
entitlement-free binary gets `errSecMissingEntitlement (-34018)` for add and delete (measured). Any
fix has to work on the legacy keychain, or the macOS head has to require a stable signing identity.

## Our workaround

We register a macOS-only `ISecureStorageService` that wraps automatic reads in
`SecKeychainSetUserInteractionAllowed(false)` / restore, maps the `OSStatus` to a typed status, and
implements replace-on-write via the legacy delete path. Details and measurements:
`e2e-evidence/macos-keychain-fix/REPORT.md` in this repo.
