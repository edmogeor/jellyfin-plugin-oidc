# Jellyfin OIDC Authentication

This GPL-3.0-or-later Jellyfin 12 plugin authenticates Jellyfin Users through one OpenID Connect Identity Provider. It targets `net10.0` and uses Jellyfin as the authority for user permissions and media activity.

## Language

**Jellyfin User**: A local Jellyfin user that owns permissions and media activity.
_Avoid_: Account.

**Identity Provider**: The OpenID Connect service that authenticates a user.
_Avoid_: SSO provider, OIDC provider.

**Provisioning**: Creating a Jellyfin User when an eligible Identity Provider identity has no Identity Link or first-sign-in match.

**Allowed Group**: An Identity Provider group permitted to sign in and provision Jellyfin Users.

**Administrator Group**: An Allowed Group whose members are Jellyfin administrators.

**Email Match**: A case-insensitive match between a verified Identity Provider email and a Jellyfin User username.

**Identity Link**: A durable binding between an Identity Provider issuer and `sub` claim and a Jellyfin User.

**Preferred Username Match**: A case-insensitive first-sign-in match between top-level `preferred_username` and Jellyfin User username, explicitly trusted by the administrator.

**Pending Admin Match**: A single-use expected verified email or preferred username reserving a Jellyfin User for a future eligible OIDC sign-in.

**Unlink opt-out**: A durable issuer + `sub` record reserving the former Jellyfin User after explicit unlinking. It blocks automatic matching and Provisioning until explicit relinking or a successful Pending Admin Match to that user.

**Open enrollment**: Both group lists are empty. Eligible identities can sign in without a group claim and always receive non-administrator status.

## Scope

- One Identity Provider is supported.
- The plugin uses OpenID Connect discovery, confidential authorization-code flow, PKCE, and HTTPS metadata.
- Supported claims are top-level `sub`, `email`, `email_verified`, `preferred_username`, `picture`, and a configurable top-level group claim. The group claim may be a string, JSON string array, or multiple claims.
- Nested group paths and manual endpoint overrides are unsupported. Map nested Identity Provider data to a top-level claim instead.
- Jellyfin Web supports self-service linking and per-user administrator management in Auto, Desktop (Legacy), and Mobile (Legacy) display modes. Other clients retain ordinary OIDC sign-in.
- SAML, LDAP, OAuth-only flows, multiple providers, and Identity Provider-initiated sign-in are unsupported.

## Configuration

OIDC is disabled by default. Enabled configuration requires:

- An absolute HTTPS issuer URL.
- A client ID and client secret.
- Either configured User Group / Administrator Group admission or deliberate Open enrollment.
- An Administrator Group when local password login is disabled for all Jellyfin Users.

The Public Jellyfin URL override is optional. When unset, callback and logout URLs use the browser request origin and path base. OIDC start and callback requests require an effective HTTPS public URL. Use an HTTPS override behind a reverse proxy or when Jellyfin has multiple public addresses.

The plugin always requests `openid`, `email`, and `profile`. Additional requested scopes are optional and space-separated. Configure them only when the Identity Provider requires a scope to return an otherwise supported claim, such as Authelia's `groups` scope.

Group lists are comma-separated exact values. Administrator Group membership grants access even when the User Group is empty. Both lists empty means Open enrollment, including with public Identity Providers such as Google. The default group claim is `groups`.

First-sign-in matching defaults to verified email. Preferred username matching is opt-in and requires the Identity Provider to keep usernames unique and non-reassignable. The setting never changes an existing Identity Link's target or username policy.

Provider branding is selected explicitly and affects presentation only. Other is the default and uses SSO with a key icon. Supported brands use locally embedded NextAuth logos where available, otherwise a key icon. A non-empty custom sign-in label overrides generated `Sign in with {Provider name}` wording.

Self-service Identity Links are enabled by default. Disabling them leaves read-only profile status, administrator management, and ordinary OIDC sign-in available, and rejects in-flight self-service completions.

Profile image synchronization is disabled by default. When enabled, the standard `picture` claim updates the Jellyfin User profile image at sign-in.

Password login modes are:

- `AllowForAllUsers`, local passwords remain available.
- `DisableForLinkedUsersOnly`, local passwords are unavailable for Jellyfin Users with an Identity Link.
- `DisableForAllUsers`, local passwords are unavailable for every Jellyfin User.

Saving configuration reapplies the selected local-password policy to all Jellyfin Users.

## Sign-In Behavior

1. The plugin validates the OpenID Connect response and requires a non-empty `sub` from the configured issuer. Configured group admission requires membership in an Allowed Group; Open enrollment requires no group claim.
2. It resolves an Identity Link by issuer + `sub` first, without requiring email or preferred username.
3. Without a link, it resolves a Pending Admin Match, then the selected first-sign-in matching rule. Explicit unlink opt-outs and reserved/owned Jellyfin Users cannot be bypassed by automatic matching.
4. Without a match, it provisions a Jellyfin User with the selected verified email or preferred username as username and an undisclosed random local password. Missing matching claims, ambiguous matches, and name collisions are denied.
5. It creates an Identity Link when needed and synchronizes administrator status from Administrator Group membership.
6. It creates a single-use, five-minute Jellyfin session handoff ticket and returns only to a local relative route.

An Identity Link is authoritative for returning identities. Email-matched and legacy links follow verified email changes, unless another Jellyfin User owns that username. Missing verified email skips renaming. Explicit links and preferred-username-matched links preserve the Jellyfin username. The plugin never merges users or reassigns media activity. A stale link to a deleted Jellyfin User is removed, then the identity is matched or provisioned again. Deletion itself is not an unlink opt-out.

Group membership is evaluated at OIDC sign-in. Removing all Allowed Groups denies the next sign-in; removing Administrator Group membership removes administrator status at the next sign-in. Existing Jellyfin sessions are not terminated in the background.

Open enrollment always sets administrator status to false, including for a returning linked administrator or identities that return unconfigured groups. It cannot be combined with disabling all local passwords.

## Identity Management

The signed-in profile shows only the authenticated Jellyfin User's Identity Provider card and last-seen claims. Administrators have an OIDC tab in Dashboard > Users > Edit User, with subject/issuer/origin details, explicit Unlink, Pending Admin Matches, and Link by signing in. Expected claims are editable only while unlinked. The supported Jellyfin 12 stack's Add User page also offers an optional expected identity entry; blank entries retain the native creation flow. A failed pending save reports partial success and opens the created user's OIDC tab.

Explicit linking uses five-minute, single-use server-side intents and completion tickets bound to the initiating Jellyfin User, session, issuer, operation, and target. The navigation callback never switches Jellyfin sessions; an authenticated completion request rechecks the initiating session and current group admission. Administrator linking additionally requires confirmation of the returned identity. Linking alone does not synchronize administrator rights or profile images.

Explicit unlinking records an opt-out and reapplies password policy. It neither ends existing Jellyfin sessions nor revokes Identity Provider sessions. Self-service unlink is forbidden when all local passwords are disabled; administrator unlink remains available with a lockout warning. Profile and administrator password controls are hidden in that mode and restored when it changes.

Identity changes and configuration saves share a process-wide gate. Browser configuration saves cannot replace server-owned links, opt-outs, or pending matches. Management writes require a current revision; XML is written to a temporary file and atomically replaced before the new configuration is published. Issuer changes preserve old issuer-scoped records and never reinterpret their subjects.

## Browser Integration And Logout

When local passwords are disabled for all users, choose whether Jellyfin Web redirects its unauthenticated sign-in page to OIDC or keeps the sign-in heading with only the OIDC button. The browser integration also shows generic sign-in failures and can intercept logout. Saving a setting that changes the sign-in state reloads Jellyfin Web; other settings update the integration without a reload.

With RP-initiated logout, a protected ID token redirects to the discovered Identity Provider end-session endpoint with `client_id` and `id_token_hint`. Jellyfin receives the post-logout redirect unless direct sign-in redirects are enabled, in which case the Identity Provider owns the page. Allow `<Public Jellyfin URL>/web/index.html` as a post-logout redirect URI when Jellyfin receives that redirect. Missing tokens, disabled RP logout, or missing end-session endpoints return to Jellyfin login; direct sign-in redirects instead show an OIDC-only signed-out state to prevent a sign-in loop.

## Security Boundaries

- Only local relative return URLs are accepted. Absolute URLs, protocol-relative URLs, and backslash paths are rejected.
- OIDC start and callback requests require an effective HTTPS public URL.
- Email matching requires verified email. `preferred_username` is an optional first-sign-in matching claim, never a durable identity key. UserInfo cannot replace the validated ID-token subject.
- Client secrets, access tokens, authorization codes, and complete ID tokens must not be logged or sent to browser configuration endpoints.
- Public browser configuration contains only presentation, password-mode, logout, and self-service settings. Identity records and last-seen claims require authenticated owner or administrator authorization.
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
