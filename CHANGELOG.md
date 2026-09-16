## [0.1.8.0]

- Make direct Identity Provider redirects opt-in when local passwords are disabled for all Jellyfin Users.
- Keep the OIDC sign-in button while removing local-login controls without direct redirects.
- Localize the redirect setting and OIDC signed-out title.

## [0.1.7.0]

- Redirect directly to OIDC when local passwords are disabled for all Jellyfin Users.
- Add a themed OIDC-only signed-out state and avoid stale SSO button redirects after browser Back navigation.
- Let the Identity Provider own the post-logout page when RP logout is enabled and local passwords are disabled.
- Change the default button label to `Sign In with SSO`.

## [0.1.6.0]

- Apply saved OIDC settings without restarting Jellyfin.

## [0.1.5.0]

- Package the OpenID Connect runtime dependencies required by Jellyfin.

## [0.1.4.0]

- Restore the Identity Link when reprovisioning after its Jellyfin User was deleted.

## [0.1.3.0]

- Clarify the local-password warning and localize its lockout risk.
- Avoid rewriting unchanged synchronized profile images.

## [0.1.2.0]

- Align the plugin and repository descriptions and list supported configuration-page languages.
- Validate HTTPS URLs directly in the OIDC configuration form.
- Add a localized Client Secret visibility toggle.

## [0.1.1.0]

- Require an HTTPS public URL for OIDC sign-in and callback requests.

## [0.1.0.0]

- OpenID Connect authorization-code sign-in with PKCE for Jellyfin Users.
- Identity Provider group-based sign-in, provisioning, administrator synchronization, and local-password policies.
- Optional profile-image synchronization and RP-initiated logout.
- Verified-email identity matching, durable Identity Links, and local-relative return URLs.
- Secret-free browser configuration and logs.
