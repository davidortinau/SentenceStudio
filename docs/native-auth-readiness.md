# Native authentication and sync readiness

Native sign-in and stored-token restore check the current authenticated access
token for one unexpired `user_profile_id` claim and require a matching
`active_profile_id` preference. Parsing the JWT claim does not validate its
signature locally; the API must authenticate each request. The presenter
leases the owner only to its in-flight readiness operation while reading the
local profile, awaiting sync, and deciding a route. The process-wide device
owner is published after an approved route and a final owner/token check.
An unconfirmed fresh account without a local profile stays on sign-in with
no published owner. A previously populated local profile may proceed offline
with pending sync indicated.

Normal (non-preview) Android registers the API auth client, secure token
store, real `SyncService`, and a transient `AuthenticatedHttpMessageHandler`
for its named CoreSync client. It currently leaves `INativeSyncReadiness` at
the native slice's fail-closed `UnconfirmedNativeSyncReadiness`; Debug preview
and iOS also remain unconfirmed. The available but unwired
`AddOwnerBoundNativeSyncReadiness()` opt-in adds a final
`OwnerBoundNativeSyncBearerHandler` to the CoreSync named client. That handler
requires a current revocable readiness lease, matching profile preference,
and matching authenticated token at each send; a published device owner
without a lease cannot authorize CoreSync. The separate
`AddSentenceStudioNativeProductionAdapters()` extension wires only the five
plan/quiz ports and does not enable readiness. Before any head opts into
sync, real two-owner transfer and server row isolation require their own proof.

The existing AppLib bearer handler still obtains the global token on each
request and can swallow token failures; only the unwired final native handler
enforces the lease and refuses anonymous or changed-owner sends. In Development,
the API may authenticate a request without a bearer as its synthetic dev user
when dev-auth fallback is enabled (or no JWT key is configured); Production
requires a JWT signing key and rejects a missing bearer. A successful
bearer check and exact-owner profile response do **not** prove that CoreSync
uploads or downloads only that owner's rows: the shared table registration
does not show a row filter, and API endpoint authorization alone is not one.
Two-owner client/server transfer and actual Android-head runtime composition
remain separate release gates. The isolated native tests exercise the opt-in
readiness adapter, owner switches, request guards, and SQLite state; they
are not evidence of head opt-in, server row isolation, or production sync.
