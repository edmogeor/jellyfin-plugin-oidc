# Jellyfin OIDC Authentication

This GPL-3.0-or-later Jellyfin 12 plugin authenticates Jellyfin Users through one OpenID Connect Identity Provider. It targets `net10.0` and uses Jellyfin as the authority for user permissions and media activity.

## Language

**Jellyfin User**: A local Jellyfin user that owns permissions and media activity.
_Avoid_: Account.

**Identity Provider**: The OpenID Connect service that authenticates a user.
_Avoid_: SSO provider, OIDC provider.

**Provider**: The shorter UI label for an Identity Provider.

**Provisioning**: Creating a Jellyfin User when an eligible Identity Provider identity has no existing Identity Link, fulfilled approval, or automatic match.

**Allowed Group**: An Identity Provider group permitted to sign in and provision Jellyfin Users.

**Administrator Group**: An Allowed Group whose members are Jellyfin administrators.

**Email Match**: A case-insensitive match between a verified Identity Provider email and a Jellyfin User username.

**Username Match**: An explicitly enabled, case-insensitive match between the configured Identity Provider username claim and a Jellyfin User username.

**Pending Link Request**: Durable administrator approval for a selected Jellyfin User's next eligible sign-in, matching exactly one of subject, current verified email, or Identity Provider username. It has no automatic expiry.

**Automatic Reattachment Block**: Durable suppression of automatic matching to an unlinked Jellyfin User.

**OIDC Session Provenance**: Evidence identifying a Jellyfin authentication record created by an OIDC session handoff, stored as its server-owned record ID, owning user ID, and token fingerprint.

**Identity Link**: A durable binding between an Identity Provider issuer and `sub` claim and a Jellyfin User.

## Scope

- One Identity Provider is supported.
- The plugin uses OpenID Connect discovery, confidential authorization-code flow, PKCE, and HTTPS metadata.
- Supported claims are top-level `sub`, `email`, `email_verified`, `picture`, a configurable top-level string username claim, and a configurable top-level group claim. The username claim defaults to `preferred_username`; the group claim may be a string, JSON string array, or multiple claims.
- Nested claim paths and manual endpoint overrides are unsupported. Map nested Identity Provider data to a top-level claim instead.
- SAML, LDAP, OAuth-only flows, multiple providers, and Identity Provider-initiated sign-in are unsupported.

## Configuration

OIDC is disabled by default. Enabled configuration requires:

- An absolute HTTPS issuer URL.
- A client ID and client secret.
- At least one configured User Group or Administrator Group.
- An Administrator Group when local password login is disabled for all Jellyfin Users.

The Provider selector records the configured Identity Provider product: Authentik, Authelia, Keycloak, Pocket ID, ZITADEL, or Other. New and older configurations without a selection default to Other. The sign-in button shows the selected provider's monochrome logo and localized `Sign In with <Provider Name>` text. Other shows a key icon and an optional Provider name field; an empty or whitespace-only name displays SSO. The custom name is retained when switching providers but only used for Other. Legacy custom button text is no longer used. These are non-secret presentation settings; they do not apply authentication presets or change issuer, scopes, claims, or Identity Links.

The Public Jellyfin URL override is optional. Issuer and public URLs must not contain credentials, queries, or fragments. When unset, callback and logout URLs use the browser request origin and path base. OIDC start and callback requests require an effective HTTPS public URL. Use an HTTPS override behind a reverse proxy or when Jellyfin has multiple public addresses.

The plugin always requests `openid`, `email`, and `profile`. Additional requested scopes are optional and space-separated. Configure them only when the Identity Provider requires a scope to return an otherwise supported claim, such as Authelia's `groups` scope.

Group lists are comma-separated exact values; lists containing only commas or whitespace have no configured groups. Administrator Group membership grants access even when the User Group is empty. The Group claim must be non-empty. The default group claim is `groups`; the default login button label is `Sign In with SSO`.

`RequireVerifiedEmail` is enabled by default. Linking and all Identity Provider sign-ins, including those with an existing Identity Link, then require a supplied non-empty email with `email_verified=true`. Disabling it permits email-free identities. Email Match always requires a supplied verified email; provisioning requires either a supplied username claim or a verified email. A previously retained email does not satisfy the requirement for a new authentication response.

The Username claim setting defaults to `preferred_username` and reads an exact top-level string claim. It supplies the linked-identity display fallback and optional Username Match. Selecting `email` uses only verified email. Username Match does not fall back to email when its claim is absent.

New-user username selection is independent of matching and display. `UsernameClaim` is the backward-compatible default, using the configured username claim and falling back to verified email when absent. `VerifiedEmail` names new Jellyfin Users only from current verified email; missing or unverified email denies provisioning even when `RequireVerifiedEmail` is off. This requirement does not affect resolution of existing links, approvals, or automatic matches. Changing the selection never renames existing Jellyfin Users.

Automatic linking has three modes: `None`, `EmailToUsername`, and `UsernameToUsername`. New installs default to `None`. Existing saved configurations retain their selected mode; older configurations without a mode retain backward-compatible `EmailToUsername` behavior. `None` disables automatic attachment to existing users, not provisioning, self-service linking, or administrator approvals. Username matching is opt-in and requires the Identity Provider's username namespace to correspond to the intended Jellyfin Users.

Profile image synchronization is disabled by default. When enabled, the standard `picture` claim updates the Jellyfin User profile image at sign-in.

Password login modes are:

- `AllowForAllUsers`, local passwords remain available.
- `DisableForLinkedUsersOnly`, local passwords are unavailable for Jellyfin Users with an Identity Link.
- `DisableForAllUsers`, local passwords are unavailable for every Jellyfin User.

Saving configuration reapplies the selected local-password policy to all Jellyfin Users before committing the settings, restoring previous authentication providers on failure. Enabled startup reconciles saved policy, and newly created Jellyfin Users receive the global restriction. Native password-authentication endpoints also reject requests while OIDC is enabled with globally disabled passwords, closing creation and reconciliation gaps. Disabled startup preserves host-level emergency recovery. The signed-in Jellyfin User's profile hides the complete password form, including save/reset controls, when local-password sign-in is disabled for that user. Unlinked users retain password controls under `DisableForLinkedUsersOnly`; all users lose them under `DisableForAllUsers`. Re-enabling local passwords restores the controls without overriding Jellyfin's own permission-based visibility.

## Sign-In Behavior

1. The plugin validates the OpenID Connect response and requires a non-empty `sub`, membership in an Allowed Group, and a supplied verified email when `RequireVerifiedEmail` is enabled.
2. It resolves an Identity Link by issuer + `sub` first. Pending approvals cannot transfer a linked identity.
3. Without a link, it evaluates Pending Link Requests for that issuer. Subject is exact and case-sensitive. Email requires current `email_verified=true`; email and username comparisons are case-insensitive, without alias normalization. Username approvals record the configured top-level Username claim at approval time; changing that claim requires editing the approval before fulfilment. Ambiguous matching approvals, disabled/deleted targets, and reserved criteria that cannot be fulfilled deny sign-in without fallback provisioning.
4. Otherwise it applies the configured automatic linking mode. Automatic matching is allowed only for a Jellyfin User without an Identity Link, active approval, or Automatic Reattachment Block. It then provisions a Jellyfin User using the selected new-user username source. An unavailable name or a case-insensitive username collision denies provisioning. The undisclosed random local password and configured password policy are present in the initial database insert.
5. It creates an Identity Link when needed and synchronizes administrator status from Administrator Group membership.
6. It creates a single-use, five-minute Jellyfin session handoff ticket bound to an HttpOnly, Secure browser nonce cookie and returns only to the validated local relative route. Redemption preserves the Remember Me choice and other servers' saved credentials. Credential-bearing index responses are private and non-cacheable.

Failed provisioning removes only the Jellyfin User created by that attempt when its Identity Link cannot be persisted, allowing a retry without a stranded username collision.

An Identity Link is authoritative for returning identities, including identities without email when the verified-email requirement is disabled. Sign-in never renames an existing Jellyfin User, even when the verified email or username claim changes. The plugin never merges users or reassigns media activity. A stale link to a deleted Jellyfin User is removed, then the eligible identity is matched or provisioned again using the same rules.

Group membership is evaluated at OIDC sign-in. Removing all Allowed Groups denies the next sign-in; removing Administrator Group membership removes administrator status at the next sign-in. Existing Jellyfin sessions are not terminated in the background.

Provisioning, explicit linking, administrator operations, session handoffs, and configuration saves share the configuration gate. A callback authenticated with replaced configuration or an older identity revision is denied and must restart. Administrator unlink, approval edits, and cancellations invalidate older callbacks and handoff tickets. Opaque per-user versions prevent stale tabs from overwriting approvals or unlinking a replacement link. Configuration saves preserve server-owned Identity Links, approvals, suppression, and session evidence.

## Self-Service Identity Provider Linking

`AllowSelfServiceLinking` controls whether signed-in Jellyfin Users can link or unlink their Provider from their own profile. It is off for new installs; older saved configurations without this setting retain self-service access. When off, existing links are read-only and new self-service links and unlinks are rejected server-side. Administrator management and retries of already-outstanding sign-out operations remain available.

The signed-in Jellyfin User's own profile page shows a Provider card above the password fields when those fields are available. Its heading uses the same monochrome icon and Provider name as the sign-in button; Other uses the key icon and defaults to SSO when its name is empty or whitespace-only. Authenticated profile status supplies this presentation even while OIDC is disabled. A link from a previous issuer uses the generic key/SSO heading rather than incorrectly naming the current provider. It displays `Linked as <email>` when a verified email is retained, falling back to `Linked as <Provider username>` when only the configured username claim is retained, or `Linked` when neither is recorded. When self-service linking is allowed, linked users see `Unlink account`; eligible unlinked users see `Link account`. Otherwise, existing links remain visible without either action. The card has no hover behavior and uses Jellyfin's shared components in modern and legacy UI.

Profile unlink and revocation retry derive ownership from the header-authenticated Jellyfin session, never a browser user ID. They share the administrator unlink implementation, version checks, suppression, password-policy rollback, and exact OIDC-token selection. The confirmation uses the same `Dashboard.confirm` component and styling as configuration-page confirmations. When self-service linking is allowed, linked users can unlink while OIDC is disabled or invalid, including links from an older issuer. Partial revocation remains visible with a retry action even when self-service linking is off. Local sessions remain active and password controls are restored subject to policy; a revoked OIDC-origin initiating session returns to sign-in without an automatic redirect loop.

Explicit linking requires a valid Jellyfin session, a validated Identity Provider issuer and non-empty `sub`, membership in an Allowed Group, and a supplied verified email when the requirement is enabled. Email Match does not choose the target Jellyfin User. The Identity Link binds directly to the initiating Jellyfin User. Existing links cannot be overwritten or transferred.

When supplied, a non-empty verified email and a non-empty configured username claim are retained on the Identity Link as display metadata during successful linking or sign-in. Subsequent supplied values refresh them; absent username or absent/unverified email does not erase the retained values. The profile displays these last-supplied values, not a live Identity Provider lookup, with email taking precedence. They never rename an existing Jellyfin User. Issuer and `sub` remain the authoritative Identity Link key.

Header-authenticated initiation creates a single-use navigation ticket. Linking requests fresh Identity Provider authentication using `prompt=login`. Protected OIDC state and correlation bind the callback to a short-lived server-side transaction. After returning to the profile, an authenticated completion request must use the same Jellyfin User and session token as initiation; the server retains only a token fingerprint. Every stage is single-use and the entire transaction expires after five minutes. Revoked sessions and replaced configuration are denied. Linking never creates a new Jellyfin session or a new Jellyfin User.

Linking preserves the username, administrator status, permissions, profile image, media activity, and current session. It immediately applies the configured local-password policy, including disabling password sign-in under `DisableForLinkedUsersOnly`. Subsequent Identity Provider sign-ins synchronize administrator status and optional profile images as usual, while preserving the username.

Fresh explicit linking respects pending reservations and existing links. It consumes a compatible approval and clears suppression; it cannot bypass an incompatible approval. Identity Provider authentication during linking preserves the existing Jellyfin session's origin.

## Administrator OIDC Management

In Dashboard > Users > selected Jellyfin User > OIDC, administrators can inspect a link, approve the next eligible sign-in, edit/cancel an approval, unlink, or retry incomplete sign-out. The approval form uses a native select for Subject (`sub`), Email, or Username and one Value input. Exactly one matching criterion is permitted. Subject is a stable provider identity identifier, not a session identifier. Approval is not an immediate Identity Link and does not change password availability. Requests remain pending across restarts until fulfilled, edited, or cancelled, independently of the main automatic linking mode.

The status row displays the Provider icon and name before the link status, separated by a subtle vertical rule. It shares the profile heading's branding and Other/key/SSO fallback, including generic branding for links from a previous issuer. Provider text remains neutral while linked status retains its success color; long content wraps on narrow screens. Approve link is disabled and visibly greyed out while its Value is empty or invalid, and restores its enabled appearance when valid.

The integration extends the tested Jellyfin Web 12.0.0 shared React user editor and reuses its native tabs, inputs, select, buttons, confirmation dialog, alerts, and notifications in modern and legacy UI. Its internal module IDs are build-specific; later Web builds require verification. Incompatible module APIs preserve the native editor and produce a compatibility warning. Selection uses the existing profile route with `?oidc=1`; native tabs, history, and themes remain owned by Jellyfin. Private, non-cacheable administrator endpoints use Jellyfin's native `RequiresElevation` policy and header-authenticated administrator sessions. Recovery is available with OIDC disabled, invalid settings, or a link from a previous issuer.

Unlink removes Identity Links and pending approvals, persists suppression and the effective password policy, then revokes only positively tracked OIDC authentication records. It preserves the Jellyfin User, username, permissions, administrator status, profile image, activity, local-password sessions, and Quick Connect sessions. Linked-only password restrictions are lifted; no password is generated or revealed. Globally disabled passwords remain disabled.

Self-unlink, including by the sole administrator, is permitted after confirmation. The dialog warns that an OIDC-origin initiating session ends, unknown passwords may prevent access, and sole-administrator recovery may require host access. The browser returns to a signed-out state without automatic Identity Provider redirection when its token is revoked.

Sessions created before provenance tracking are unclassified and preserved. The plugin cannot selectively revoke historical OIDC sessions without reliable evidence. Revocation targets underlying authentication records, not merely visible sessions, and does not sign out at the Identity Provider or guarantee immediate cancellation of buffered media playback. Outstanding revocations persist with the unlink's exact token selection; retries never select newer sessions. Partial failures remain visible until resolved and do not restore the link.

Suppression is user-specific, not an identity-wide ban. A fresh explicit link or fulfilled approval clears it. An otherwise eligible identity may provision a different Jellyfin User when ordinary naming rules permit it.

## Browser Integration And Logout

When local passwords are disabled for all users, choose whether Jellyfin Web redirects its unauthenticated sign-in page to OIDC or keeps the sign-in heading with only the OIDC button. The browser integration also shows generic sign-in failures and can intercept logout. Saving a setting that changes the sign-in state reloads Jellyfin Web; other settings update the integration without a reload.

With RP-initiated logout, a protected ID token redirects to the discovered Identity Provider end-session endpoint with `client_id` and `id_token_hint`. Jellyfin receives the post-logout redirect unless direct sign-in redirects are enabled, in which case the Identity Provider owns the page. Allow `<Public Jellyfin URL>/web/index.html` as a post-logout redirect URI when Jellyfin receives that redirect. Missing tokens, disabled RP logout, or missing end-session endpoints return to Jellyfin login; direct sign-in redirects instead show an OIDC-only signed-out state to prevent a sign-in loop.

Protected logout tokens use chunked HttpOnly cookies to support large ID tokens. Browser logout uses only the active server's token and clears only its credentials, including on network failure. An unconfirmed server logout shows a localized warning on the signed-out page rather than claiming successful server revocation.

## Security Boundaries

- Only local relative return URLs are accepted. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.
- OIDC start and callback requests require an effective HTTPS public URL.
- Automatic attachment uses only the explicitly selected matching mode. Email always requires current verification. Username matching is opt-in. Once attached, issuer + `sub` remains authoritative regardless of the initial matching key.
- Client secrets, access tokens, authorization codes, and complete ID tokens must not be logged or sent to browser configuration endpoints.
- Anonymous browser configuration receives only the non-secret Provider selection, optional Other provider name, password-mode, and logout settings. Self-service status exposes retained email and username only to that Jellyfin User, never issuer or subject. Elevated administrators can inspect selected-user display metadata and pending criteria. Identity values and authentication tokens must not be logged; administration logs contain actor/target user IDs and outcomes only.
- Provisioned local passwords are random and undisclosed.
- When profile image synchronization is enabled, the optional `picture` claim must be an HTTPS URL resolving to a public address or the configured Identity Provider host. Its image is downloaded with redirects disabled, a five-megabyte limit, and a ten-second deadline covering connection, headers, and body reads, and synchronized at sign-in.

## Development

Run `make help` for the command list.

- `make setup` restores CSharpier and installs locked e2e Node dependencies.
- `make format` applies CSharpier and Prettier.
- `make lint` runs Roslyn analyzers and oxlint.
- `make check` verifies formatting and linting.
- `make test-unit` runs the .NET unit suite.
- `make test-e2e` resets the Docker stack and runs headless Playwright tests.
- `make test` runs unit tests followed by e2e tests.
- `make up` starts the e2e stack without deleting state for manual testing.

The e2e stack runs Jellyfin 12, Keycloak, and Caddy on `https://localhost:8443`. It seeds `user` with password `password`, verified email `oidc-test@example.test`, and the `jellyfin-users` and `jellyfin-admins` groups. The initial local Jellyfin administrator is `root` with an empty password.

Translation JSON files are embedded in the plugin DLL. Build-time generation inserts English from `en-us.json` into the configuration HTML and injected browser script; runtime locale loading replaces those defaults. The JSON files are the source of truth for profile and administrator copy, including fallback text.

Monochrome Authentik, Authelia, Keycloak, Pocket ID, and ZITADEL SVGs are bundled in `Web/Providers` and embedded as `Jellyfin.Plugin.Oidc.Web.Providers.<Provider>.svg`. Anonymous `/oidc/icons/<Provider>.svg` endpoints serve these static assets locally. The sign-in button uses CSS masks with `currentColor`, matching its text color in normal, hover, and disabled states; Other uses Jellyfin's key glyph. The icon remains visible while the button text changes to Redirecting. Pinned upstream sources, modifications, and third-party notices are recorded in `NOTICE`; plugin packages include the notices and applicable license texts.

CI runs formatting checks, Roslyn analyzers, oxlint, unit tests, and reset e2e tests. Playwright failure artifacts are retained.
