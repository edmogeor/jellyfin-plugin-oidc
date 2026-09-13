# Product Requirements Document: Jellyfin OIDC Authentication

## 1. Purpose

Build a GPL-3.0-or-later Jellyfin 12 plugin that authenticates Jellyfin Users through one OpenID Connect Identity Provider.

The plugin must be secure by default, require little configuration, work with the Jellyfin dashboard and supported clients, and keep Jellyfin as the owner of user permissions and media activity.

## 2. Goals

- Support exactly one OpenID Connect Identity Provider.
- Configure the plugin from the Jellyfin dashboard.
- Authenticate with the authorization-code flow, a confidential client secret, and PKCE.
- Match an Identity Provider identity to a Jellyfin User by verified email and username.
- Provision eligible Identity Provider identities as Jellyfin Users.
- Map two optional Identity Provider groups to standard-user and administrator access.
- Support direct browser OIDC login and Quick Connect.
- Optionally disable local credential login without disabling Quick Connect.
- Fit custom UI into Jellyfin Web's existing visual language.

## 3. Non-Goals

- Multiple Identity Providers.
- SAML, LDAP, OAuth-only, or device-authorization flows.
- Custom Jellyfin permission, library, Live TV, or role mapping.
- A custom Jellyfin User email or display-name field.
- A separate frontend design system.
- Back-Channel or Front-Channel Logout.
- Identity Provider-initiated, unsolicited sign-in.
- User self-service linking or unlinking.
- Manual OIDC endpoint, issuer-validation, endpoint-validation, or HTTPS bypasses.

## 4. Terminology

- **Jellyfin User**: A local Jellyfin account that owns permissions and media activity.
- **Identity Provider**: The OpenID Connect service that authenticates a user.
- **Email Match**: A case-insensitive match between a verified Identity Provider email and a Jellyfin User username.
- **Identity Link**: A binding from the Identity Provider `sub` claim to a Jellyfin User.
- **Provisioning**: Creating a Jellyfin User when an eligible Identity Provider identity has no Email Match.
- **Allowed Group**: An Identity Provider group permitted to access Jellyfin.
- **Administrator Group**: An Allowed Group whose members are Jellyfin administrators.

## 5. Product Model

### 5.1 Identity Provider

The plugin supports one configured Identity Provider. The administrator supplies only the issuer URL, and the plugin obtains authorization, token, JWKS, UserInfo, and logout endpoints through OpenID Connect discovery.

The Identity Provider client is confidential. It requires a client ID and client secret. The plugin uses authorization code flow with PKCE.

### 5.2 Jellyfin User Identity

For OIDC users, the Jellyfin username is an email address. It is also the name shown by Jellyfin clients because Jellyfin has no distinct built-in display-name field.

Local-only Jellyfin Users can continue to use arbitrary usernames. A local Jellyfin User may use OIDC only if their username case-insensitively equals the Identity Provider's verified email.

The plugin uses two identity stages:

1. An unlinked eligible identity is matched to a Jellyfin User through Email Match.
2. A successful Email Match creates an Identity Link keyed by the Identity Provider `sub` claim.

The Identity Link is authoritative for returning identities. Email is the initial matching and provisioning key, not a replacement for `sub`.

### 5.3 Email Changes

When a linked identity returns with the same `sub` and a new verified email, the plugin updates the Jellyfin username to the new email.

The rename is refused if another Jellyfin User already uses the new email username. The plugin never merges users, transfers media activity, or reassigns identities automatically.

### 5.4 Provisioning

An eligible identity with no Identity Link and no Email Match is provisioned as a Jellyfin User:

- Username is the verified email.
- The Jellyfin User receives a cryptographically random, undisclosed local password.
- Permissions use Jellyfin's normal new-user defaults.
- The plugin applies administrator status only when the identity belongs to the configured Administrator Group.

Library and other user permissions remain managed in Jellyfin's native user-management UI.

## 6. Configuration

The plugin has a single dashboard configuration page. OIDC is disabled by default. Saving configuration does not enable OIDC unless the administrator explicitly enables it.

The page presents the required setup first. A collapsed **Advanced Options** section contains settings that change claim layout, login behavior, or logout behavior. It must not contain security-bypass settings.

| Setting | Required | Default | Requirement |
| --- | --- | --- | --- |
| Enable OIDC | Yes to activate | Off | OIDC routes and login UI remain inactive until enabled. |
| Public Jellyfin URL | Yes | None | HTTPS public URL used to construct fixed callback and signed-out URLs. |
| Issuer URL | Yes | None | HTTPS Identity Provider issuer used for discovery. |
| Client ID | Yes | None | Confidential OIDC client ID. |
| Client secret | Yes | None | Confidential OIDC client secret. |
| Group claim | No | `groups` | Name of a top-level string or string-array claim containing group identifiers. Configured in Advanced Options. |
| Group scope | No | Empty | Extra scope requested in addition to `openid email` when the provider requires it to release groups. Configured in Advanced Options. |
| User Group | No | Empty | Group whose members receive standard Jellyfin User access. |
| Administrator Group | No | Empty | Group whose members receive Jellyfin administrator access. |
| Login button text | No | `Login with SSO` | Text shown on the OIDC login button. Configured in Advanced Options. |
| Password login mode | Yes | Allow for all users | Defines local credential availability. Configured in Advanced Options. |
| RP-Initiated Logout | No | Off | Ends the Identity Provider browser session when supported. Configured in Advanced Options. |

At least one of User Group or Administrator Group is required. The Administrator Group is also an Allowed Group. Group identifiers are exact values issued by the Identity Provider.

The plugin always requests `openid` and `email`, and also requests the optional Group scope when configured. It reads the configured top-level group claim from the ID token or UserInfo response. Nested claim paths, such as `realm_access.roles`, are not supported. Identity Provider administrators must map nested roles or groups to a top-level claim.

The public URL and issuer URL must use HTTPS. Loopback HTTP is allowed only by the automated test environment, not by administrator configuration.

The plugin must not offer manual authorization, token, JWKS, UserInfo, or logout endpoint fields. It must not offer switches that disable HTTPS, issuer validation, or discovered-endpoint validation.

### 6.1 Advanced Options

Advanced Options contains:

- **Group claim**, default `groups`, for providers that use another top-level claim such as `roles`.
- **Group scope**, for providers that require an additional scope before releasing group membership.
- **Login button text**, default `Login with SSO`.
- **Password login mode**.
- **RP-Initiated Logout**.

The section must explain that nested group claims require an Identity Provider-side mapper. It must not expose manual endpoint overrides, HTTP support, relaxed discovery validation, public-client mode, nested-claim paths, or protocol-flow switches.

### 6.2 Password Login Modes

The dashboard exposes one setting with these values:

| Mode | Behavior |
| --- | --- |
| Allow for all users | Default. Local credential login remains available. |
| Disable for linked users only | Linked Jellyfin Users cannot use local credentials. Local-only users retain normal login. |
| Disable for all users | All local credential flows are rejected. OIDC, Quick Connect, existing sessions, and API keys remain available. |

Local credential flows include username/password, quick login, PIN, and easy-password authentication. Quick Connect must remain available in every mode.

The plugin must reject enabling the global mode unless OIDC is enabled, configuration is valid, and an Administrator Group is configured.

## 7. Authentication Flows

### 7.1 Browser Login

When OIDC is enabled and local credentials are allowed, the Jellyfin login UI displays an explicit OIDC button using the configured label.

When the global password-login mode is active, the Jellyfin login UI immediately starts an OIDC authorization request. If the Identity Provider has a session, the user signs in without interacting with Jellyfin's login page. If OIDC fails, Jellyfin displays a generic retryable error instead of repeatedly redirecting.

The authorization request and callback must use a single-use, expiring, state-correlated flow. Unsolicited Identity Provider callbacks are rejected.

### 7.2 Eligibility and Matching

After a successful OIDC callback, the plugin must:

1. Validate the OIDC response, issuer, audience, signature, nonce, state, PKCE exchange, and expiry.
2. Require a non-empty `sub` claim.
3. Require a non-empty `email` claim and `email_verified` set to `true`.
4. Extract groups from the configured top-level claim.
5. Deny the sign-in unless the identity belongs to the User Group or Administrator Group.
6. Resolve an existing Identity Link by `sub`.
7. If no link exists, resolve an Email Match by case-insensitive verified email and Jellyfin username.
8. If neither exists, provision a Jellyfin User.
9. Create or update the Identity Link.
10. Apply the current administrator status from Administrator Group membership.
11. Create a Jellyfin session and return the user to the requested local Jellyfin route.

If the group membership no longer includes either Allowed Group, the next OIDC sign-in is denied. The plugin removes administrator status when the Administrator Group no longer matches. It does not poll the Identity Provider or terminate active Jellyfin sessions in the background.

### 7.3 Native Apps and Quick Connect

Compatible clients use the browser OIDC flow and receive the Jellyfin session handoff after callback completion.

Clients without an in-app OIDC browser flow use Jellyfin Quick Connect. A user approves Quick Connect from an OIDC-authenticated browser session. Quick Connect remains available when all local credential flows are disabled.

### 7.4 Logout

Normal Jellyfin logout always ends the Jellyfin session.

When the optional RP-Initiated Logout setting is enabled and the Identity Provider advertises an `end_session_endpoint`, normal logout for an OIDC-authenticated browser session also starts RP-Initiated Logout. The request uses the session's `id_token_hint` and the configured public signed-out callback URL.

When the setting is disabled, the user has no OIDC browser session, or the Identity Provider lacks an end-session endpoint, logout remains Jellyfin-only.

## 8. User Interface

### 8.1 Visual Requirements

All custom UI must use Jellyfin Web's established components, CSS classes, typography, spacing, theme variables, accessibility conventions, and interaction patterns. The plugin must not introduce a separate visual system.

### 8.2 Jellyfin Web Integration

Jellyfin's plugin API exposes standalone configuration pages but does not provide a supported extension point in its native login or user-profile pages. The plugin therefore uses a small, targeted Jellyfin Web injection mechanism:

- An ASP.NET Core startup filter registers middleware that injects one plugin script into Jellyfin Web's `index.html`.
- The script injects the OIDC login button, global-mode auto-start behavior, logout integration, and generic failure feedback.
- The script observes Jellyfin's rendered UI and uses resilient, version-tested anchors.
- The middleware must alter only Jellyfin Web index requests, preserve all unrelated responses, and avoid duplicate injection.

The integration targets Jellyfin 12 only. Every supported Jellyfin Web upgrade requires regression testing of the injection behavior.

### 8.3 Error Feedback

Users receive generic Jellyfin-styled messages, such as “Sign-in not permitted.” The UI must not disclose whether a Jellyfin User exists, whether an email matched, whether a group is missing, or why a link failed.

Server logs contain actionable diagnostic details without logging access tokens, refresh tokens, authorization codes, client secrets, or complete ID tokens.

## 9. Security Requirements

- Use `Microsoft.AspNetCore.Authentication.OpenIdConnect`, which uses the Microsoft IdentityModel token stack.
- Use authorization code flow with a confidential client secret and PKCE.
- Require OpenID Connect discovery from the configured HTTPS issuer.
- Validate issuer, audience, signing keys, signature, nonce, state, PKCE, expiry, and callback correlation.
- Use exact fixed callback URLs derived from the configured Public Jellyfin URL, never from request host headers.
- Permit only local relative return URLs. Reject absolute, protocol-relative, and otherwise external redirects.
- Require `email_verified: true`; never match on an unverified email or `preferred_username`.
- Treat `sub` as the stable linked identity; never use `preferred_username` as an identity key.
- Do not create, merge, or reassign a Jellyfin User when email matching is ambiguous or collides during a rename.
- Restrict configuration and identity-management endpoints to Jellyfin administrators.
- Keep client secrets and OIDC tokens out of logs and browser-visible responses.
- Generate random passwords for provisioned Jellyfin Users and never expose them.
- Preserve Quick Connect, existing sessions, and API keys when local credential flows are disabled.

## 10. Technical Constraints

- Target Jellyfin 12 and `net10.0`.
- Use `Microsoft.AspNetCore.Authentication.OpenIdConnect` for the OIDC flow.
- Use Jellyfin's built-in user, session, policy, authentication-provider, and Quick Connect APIs.
- Use the configured Identity Provider as a single source for OIDC discovery and group claims.
- Store Identity Links durably as plugin-owned data keyed by Identity Provider `sub` and Jellyfin User ID.
- Support one Identity Provider only. Configuration changes affecting OIDC handler options must apply predictably, with a server restart if required by Jellyfin's plugin lifecycle.

## 11. Observability

The plugin logs structured, non-secret events for:

- Configuration validation failures.
- Discovery and token-validation failures.
- Group authorization denial.
- Provisioning, automatic linking, email rename, and collision denial.
- Administrator-status changes.
- Password-login policy changes and rejected local credentials.
- RP-Initiated Logout discovery and redirect failures.

## 12. Testing

### 12.1 Unit Tests

Unit tests must cover:

- Configuration validation and enablement guards.
- Group-claim parsing and group authorization.
- Email Match and Identity Link resolution.
- Provisioning with a random local password.
- Email-change rename and collision rejection.
- Administrator status synchronization.
- Password-login mode enforcement.
- Return URL validation.

### 12.2 End-to-End Tests

Docker end-to-end tests use Authentik and Jellyfin 12 to verify:

- Valid OIDC login through the browser.
- Rejection of identities without a verified email or Allowed Group.
- Provisioning and normal Jellyfin default permissions.
- Email Match to an existing Jellyfin User.
- Identity Link reuse by `sub`.
- Identity Provider email rename and collision denial.
- Standard-user and administrator-group mapping.
- Direct browser client handoff and Quick Connect.
- Each password-login mode.
- RP-Initiated Logout when enabled.
- Login and profile UI injection behavior.

## 13. Acceptance Criteria

1. A Jellyfin administrator can configure one OIDC Identity Provider from the dashboard and explicitly enable it.
2. The plugin rejects non-HTTPS issuer and public URLs in production configuration.
3. An eligible identity with a verified email can sign in, receive a Jellyfin session, and retain the same Jellyfin User on future sign-ins through `sub`.
4. An eligible identity with no matching Jellyfin User is provisioned using its verified email as the username and an undisclosed random password.
5. An existing Jellyfin User with a matching email username is automatically linked on first eligible OIDC sign-in.
6. A missing, unverified, ambiguous, or colliding email never authenticates or provisions a Jellyfin User.
7. User Group members are standard Jellyfin Users and Administrator Group members are administrators after each OIDC sign-in.
8. Identities outside both configured groups receive only a generic denial message and no Jellyfin session.
9. A linked identity with a changed verified email renames its Jellyfin User when unused, and is denied when the target username is occupied.
10. The three password-login modes behave as specified, and global disablement cannot be enabled without an Administrator Group.
11. Global password disablement automatically starts OIDC rather than exposing a normal Jellyfin login screen.
12. Quick Connect works in every password-login mode.
13. Optional RP-Initiated Logout is used only when enabled and supported by discovery.
14. Custom login, callback, and failure UI follows Jellyfin's existing visual language.
15. The Docker end-to-end suite passes against Jellyfin 12 and Authentik.

## 14. Future Considerations

These are intentionally deferred:

- Back-Channel Logout for Identity Provider-driven session revocation.
- A dedicated administrator Identity Link reset action.
- Multi-provider linking and provider selection.
- Nested group claim paths.
- Full Jellyfin permission mapping from Identity Provider groups.
- A core Jellyfin display-name or email-field contribution.
