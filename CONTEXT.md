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
- Supported claims are top-level `sub`, `email`, `email_verified`, `picture`, and a configurable top-level group claim. The group claim may be a string, JSON string array, or multiple claims.
- Nested group paths and manual endpoint overrides are unsupported. Map nested Identity Provider data to a top-level claim instead.
- SAML, LDAP, OAuth-only flows, multiple providers, self-service linking, and Identity Provider-initiated sign-in are unsupported.

## Configuration

OIDC is disabled by default. Enabled configuration requires:

- An absolute HTTPS issuer URL.
- A client ID and client secret.
- At least one configured User Group or Administrator Group.
- An Administrator Group when local password login is disabled for all Jellyfin Users.

The Public Jellyfin URL override is optional. When unset, callback and logout URLs use the browser request origin and path base. OIDC start and callback requests require an effective HTTPS public URL. Use an HTTPS override behind a reverse proxy or when Jellyfin has multiple public addresses.

The plugin always requests `openid`, `email`, and `profile`. Additional requested scopes are optional and space-separated. Configure them only when the Identity Provider requires a scope to return an otherwise supported claim, such as Authelia's `groups` scope.

Group lists are comma-separated exact values. Administrator Group membership grants access even when the User Group is empty. The default group claim is `groups`; the default login button label is `Sign In with SSO`.

Profile image synchronization is disabled by default. When enabled, the standard `picture` claim updates the Jellyfin User profile image at sign-in.

Password login modes are:

- `AllowForAllUsers`, local passwords remain available.
- `DisableForLinkedUsersOnly`, local passwords are unavailable for Jellyfin Users with an Identity Link.
- `DisableForAllUsers`, local passwords are unavailable for every Jellyfin User.

Saving configuration reapplies the selected local-password policy to all Jellyfin Users.

## Sign-In Behavior

1. The plugin validates the OpenID Connect response and requires a non-empty `sub`, a non-empty verified email, and membership in an Allowed Group.
2. It resolves an Identity Link by `sub` first.
3. Without a link, it resolves an Email Match by verified email and Jellyfin username.
4. Without either, it provisions a Jellyfin User with the verified email as username and an undisclosed random local password.
5. It creates an Identity Link when needed and synchronizes administrator status from Administrator Group membership.
6. It creates a single-use, five-minute Jellyfin session handoff ticket and returns only to a local relative route.

An Identity Link is authoritative for returning identities. If its verified email changes, the linked Jellyfin User is renamed unless another Jellyfin User already owns that username. The plugin never merges users or reassigns media activity. A stale link to a deleted Jellyfin User is removed, then the identity is matched or provisioned again.

Group membership is evaluated at OIDC sign-in. Removing all Allowed Groups denies the next sign-in; removing Administrator Group membership removes administrator status at the next sign-in. Existing Jellyfin sessions are not terminated in the background.

## Browser Integration And Logout

When local passwords are disabled for all users, an optional setting redirects Jellyfin Web index requests directly to OIDC. With that setting off, the injected script removes Jellyfin's local-login controls and retains the OIDC sign-in button. It also handles generic sign-in failure and optional Jellyfin logout interception.

With RP-initiated logout enabled, a browser session with a protected ID token is redirected to the discovered Identity Provider end-session endpoint with `client_id` and `id_token_hint`. It sends a Jellyfin login return URL unless the direct-redirect setting is enabled while local passwords are disabled for all Jellyfin Users. The Identity Provider must allow `<Public Jellyfin URL>/web/index.html` as a post-logout redirect URI when that URL is sent. With direct redirect enabled, the Identity Provider owns the post-logout page. Without a token, enabled setting, or end-session endpoint, logout returns to Jellyfin login. When RP-initiated logout is disabled and direct redirect is enabled, it instead shows an OIDC-only signed-out state, preventing an immediate sign-in loop.

## Security Boundaries

- Only local relative return URLs are accepted. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.
- OIDC start and callback requests require an effective HTTPS public URL.
- Verified email is the only email matching key. `preferred_username` is never an identity key.
- Client secrets, access tokens, authorization codes, and complete ID tokens must not be logged or sent to browser configuration endpoints.
- The browser receives only non-secret login label, password-mode, and logout settings.
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

The e2e stack runs Jellyfin 12, Keycloak, and Caddy on `https://localhost:8443`. It seeds `oidc-test` with password `oidc-test-password`, verified email `oidc-test@example.test`, and the `jellyfin-users` and `jellyfin-admins` groups. The initial local Jellyfin administrator is `root` with an empty password.

CI runs formatting checks, Roslyn analyzers, oxlint, unit tests, and reset e2e tests. Playwright failure artifacts are retained.
