# Jellyfin OIDC Authentication

This GPL-3.0-or-later Jellyfin 12 plugin authenticates Jellyfin Users through one OpenID Connect Identity Provider. It targets `net10.0` and uses Jellyfin as the authority for user permissions and media activity.

## Language

**Jellyfin User**: A local Jellyfin user that owns permissions and media activity.
_Avoid_: Account.

**Identity Provider**: The OpenID Connect service that authenticates a user.
_Avoid_: SSO provider, OIDC provider.

**Provisioning**: Creating a Jellyfin User when an eligible Identity Provider identity has no Email Match.

**Allowed Group**: An Identity Provider group permitted to sign in and provision Jellyfin Users.

**Administrator Group**: An Allowed Group whose members are Jellyfin administrators.

**Email Match**: A case-insensitive match between a verified Identity Provider email and a Jellyfin User username.

**Identity Link**: A durable binding between an Identity Provider issuer and `sub` claim and a Jellyfin User.

## Scope

- One Identity Provider is supported.
- The plugin uses OpenID Connect discovery, confidential authorization-code flow, PKCE, and HTTPS metadata.
- Supported claims are top-level `sub`, `email`, `email_verified`, `picture`, a configurable top-level string username claim, and a configurable top-level group claim. The username claim defaults to `preferred_username`; the group claim may be a string, JSON string array, or multiple claims.
- Nested claim paths and manual endpoint overrides are unsupported. Map nested Identity Provider data to a top-level claim instead.
- SAML, LDAP, OAuth-only flows, multiple providers, self-service unlinking, and Identity Provider-initiated sign-in are unsupported.

## Configuration

OIDC is disabled by default. Enabled configuration requires:

- An absolute HTTPS issuer URL.
- A client ID and client secret.
- At least one configured User Group or Administrator Group.
- An Administrator Group when local password login is disabled for all Jellyfin Users.

The Public Jellyfin URL override is optional. When unset, callback and logout URLs use the browser request origin and path base. OIDC start and callback requests require an effective HTTPS public URL. Use an HTTPS override behind a reverse proxy or when Jellyfin has multiple public addresses.

The plugin always requests `openid`, `email`, and `profile`. Additional requested scopes are optional and space-separated. Configure them only when the Identity Provider requires a scope to return an otherwise supported claim, such as Authelia's `groups` scope.

Group lists are comma-separated exact values. Administrator Group membership grants access even when the User Group is empty. The default group claim is `groups`; the default login button label is `Sign In with SSO`.

`RequireVerifiedEmail` is enabled by default. Linking and all Identity Provider sign-ins, including those with an existing Identity Link, then require a supplied non-empty email with `email_verified=true`. Disabling it permits email-free identities. Email Match always requires a supplied verified email; provisioning requires either a supplied username claim or a verified email. A previously retained email does not satisfy the requirement for a new authentication response.

The Username claim setting defaults to `preferred_username` and reads an exact top-level string claim. It supplies the username for newly provisioned Jellyfin Users and the linked-identity display fallback. If absent, provisioning uses verified email. Selecting `email` uses only verified email. The setting does not change existing Jellyfin usernames or identity resolution.

Profile image synchronization is disabled by default. When enabled, the standard `picture` claim updates the Jellyfin User profile image at sign-in.

Password login modes are:

- `AllowForAllUsers`, local passwords remain available.
- `DisableForLinkedUsersOnly`, local passwords are unavailable for Jellyfin Users with an Identity Link.
- `DisableForAllUsers`, local passwords are unavailable for every Jellyfin User.

Saving configuration reapplies the selected local-password policy to all Jellyfin Users. The signed-in Jellyfin User's profile hides the complete password form, including save/reset controls, when local-password sign-in is disabled for that user. Unlinked users retain password controls under `DisableForLinkedUsersOnly`; all users lose them under `DisableForAllUsers`. Re-enabling local passwords restores the controls without overriding Jellyfin's own permission-based visibility.

## Sign-In Behavior

1. The plugin validates the OpenID Connect response and requires a non-empty `sub`, membership in an Allowed Group, and a supplied verified email when `RequireVerifiedEmail` is enabled.
2. It resolves an Identity Link by `sub` first.
3. Without a link, it attempts Email Match only when a non-empty verified email is supplied. Automatic matching is permitted only for a Jellyfin User without an existing Identity Link; a match already linked to another identity is denied.
4. Without either, it provisions a Jellyfin User with the configured username claim as username, falling back to verified email when the claim is absent. Missing both values or a case-insensitive collision with another Jellyfin username denies provisioning; the username claim never attaches an unrelated Jellyfin User. The undisclosed random local password and configured local-password policy are present in the initial database insert, so a partial failure cannot leave a passwordless user.
5. It creates an Identity Link when needed and synchronizes administrator status from Administrator Group membership.
6. It creates a single-use, five-minute Jellyfin session handoff ticket and returns only to a local relative route.

An Identity Link is authoritative for returning identities, including identities without email when the verified-email requirement is disabled. Sign-in never renames an existing Jellyfin User, even when the verified email or username claim changes. The plugin never merges users or reassigns media activity. A stale link to a deleted Jellyfin User is removed, then the eligible identity is matched or provisioned again using the same rules.

Group membership is evaluated at OIDC sign-in. Removing all Allowed Groups denies the next sign-in; removing Administrator Group membership removes administrator status at the next sign-in. Existing Jellyfin sessions are not terminated in the background.

Provisioning, explicit linking, and administrator configuration saves are serialized. A callback authenticated with a replaced configuration is denied and must restart. Sign-in and linking persist Identity Link changes without replacing configuration or reverting administrator settings. An administrator must explicitly clear an old Identity Link before attaching a recreated Identity Provider identity with a new `sub`.

## Self-Service Identity Provider Linking

The signed-in Jellyfin User's own profile page shows an `SSO` card above the password fields when those fields are available. It displays `Linked as <email>` when a verified email is retained, falling back to `Linked as <Identity Provider username>` when only the configured username claim is retained, or `Linked` when neither is recorded. Linked status has a green tick; unlinked users see a red cross with `Not linked` and a right-aligned `Link account` button with a leading link icon. The card itself has no hover behavior. Both modern UI and legacy UI use Jellyfin's shared profile components and classes. Self-service unlinking is unsupported.

Explicit linking requires a valid Jellyfin session, a validated Identity Provider issuer and non-empty `sub`, membership in an Allowed Group, and a supplied verified email when the requirement is enabled. Email Match does not choose the target Jellyfin User. The Identity Link binds directly to the initiating Jellyfin User. Existing links cannot be overwritten or transferred.

When supplied, a non-empty verified email and a non-empty configured username claim are retained on the Identity Link as display metadata during successful linking or sign-in. Subsequent supplied values refresh them; absent username or absent/unverified email does not erase the retained values. The profile displays these last-supplied values, not a live Identity Provider lookup, with email taking precedence. They never rename an existing Jellyfin User. Issuer and `sub` remain the authoritative Identity Link key.

Header-authenticated initiation creates a single-use navigation ticket. Linking requests fresh Identity Provider authentication using `prompt=login`. Protected OIDC state and correlation bind the callback to a short-lived server-side transaction. After returning to the profile, an authenticated completion request must use the same Jellyfin User and session token as initiation; the server retains only a token fingerprint. Every stage is single-use and the entire transaction expires after five minutes. Revoked sessions and replaced configuration are denied. Linking never creates a new Jellyfin session or a new Jellyfin User.

Linking preserves the username, administrator status, permissions, profile image, media activity, and current session. It immediately applies the configured local-password policy, including disabling password sign-in under `DisableForLinkedUsersOnly`. Subsequent Identity Provider sign-ins synchronize administrator status and optional profile images as usual, while preserving the username.

## Browser Integration And Logout

When local passwords are disabled for all users, choose whether Jellyfin Web redirects its unauthenticated sign-in page to OIDC or keeps the sign-in heading with only the OIDC button. The browser integration also shows generic sign-in failures and can intercept logout. Saving a setting that changes the sign-in state reloads Jellyfin Web; other settings update the integration without a reload.

With RP-initiated logout, a protected ID token redirects to the discovered Identity Provider end-session endpoint with `client_id` and `id_token_hint`. Jellyfin receives the post-logout redirect unless direct sign-in redirects are enabled, in which case the Identity Provider owns the page. Allow `<Public Jellyfin URL>/web/index.html` as a post-logout redirect URI when Jellyfin receives that redirect. Missing tokens, disabled RP logout, or missing end-session endpoints return to Jellyfin login; direct sign-in redirects instead show an OIDC-only signed-out state to prevent a sign-in loop.

## Security Boundaries

- Only local relative return URLs are accepted. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.
- OIDC start and callback requests require an effective HTTPS public URL.
- Verified email is the only automatic matching key. The configured username claim, including `preferred_username`, is never an identity matching key.
- Client secrets, access tokens, authorization codes, and complete ID tokens must not be logged or sent to browser configuration endpoints.
- Anonymous browser configuration receives only non-secret login label, password-mode, and logout settings. Private, non-cacheable link status exposes the retained email and username only to the linked Jellyfin User, never issuer or subject. Retained emails and usernames must not be logged.
- Provisioned local passwords are random and undisclosed.
- When profile image synchronization is enabled, the optional `picture` claim must be an HTTPS URL resolving to a public address or the configured Identity Provider host. Its image is downloaded with redirects disabled and synchronized at sign-in.

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

CI runs formatting checks, Roslyn analyzers, oxlint, unit tests, and reset e2e tests. Playwright failure artifacts are retained.
