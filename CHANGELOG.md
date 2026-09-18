## [0.1.24.0]

- Redirect directly to the Identity Provider without showing a loading spinner.

## [0.1.23.0]

- Prevent duplicate Identity Provider redirects while Jellyfin updates the login page.

## [0.1.22.0]

- Keep OIDC sign-in failure notifications dismissed after Jellyfin normalizes the error URL.

## [0.1.21.0]

- Paint Jellyfin's loading spinner before direct Identity Provider navigation.
- Show each OIDC sign-in failure message once per error-route visit.

## [0.1.20.0]

- Show Jellyfin's loading spinner during direct Identity Provider redirects.

## [0.1.19.0]

- Restore no-flash direct Identity Provider redirects and show OIDC-only controls after a sign-in failure.

## [0.1.18.0]

- Keep OIDC and signed-out controls on Jellyfin's rebuilt login view after selecting a server.

## [0.1.17.0]

- Keep OIDC sign-in available after Jellyfin rebuilds the login view and prevent a blank page after an interrupted direct redirect.

## [0.1.16.0]

- Keep the loading indicator visible until direct OIDC redirection completes.
- Preserve Jellyfin credentials through OIDC logout and keep the signed-out page stable across login-route navigation.

## [0.1.15.0]

- Preserve Jellyfin login controls while hiding local actions in OIDC-only and signed-out states, preventing persistent loading and retaining Quick Connect.

## [0.1.14.0]

- Preserve Jellyfin login initialization while removing unavailable local controls, restoring Quick Connect and native Change Server visibility.

## [0.1.13.0]

- Prevent Jellyfin Web from rendering the local sign-in page before a direct Identity Provider redirect.

## [0.1.12.0]

- Redirect direct visits to the Jellyfin Web root route to the Identity Provider when local passwords are disabled.

## [0.1.11.0]

- Redirect first-time Jellyfin Web login visits to the Identity Provider before the sign-in page renders.
- Make the OIDC sign-in action primary when it is the only available sign-in method, including the signed-out page.

## [0.1.10.0]

- Keep Quick Connect, Change Server, and the login disclaimer available, and make OIDC sign-in the primary action when local passwords are disabled without direct redirects.

## [0.1.9.0]

- Keep the Jellyfin sign-in heading while showing only the OIDC button when local passwords are disabled for all Jellyfin Users without direct redirects.
- Reload Jellyfin Web after saving OIDC settings so the first logout uses the updated login state.
- Do not redirect active Jellyfin Web sessions to OIDC after a page refresh.

## [0.1.8.0]

- Make direct Identity Provider redirects opt-in when local passwords are disabled for all Jellyfin Users.
- Keep the OIDC sign-in button while removing local-login controls without direct redirects.
- Localize the redirect setting and OIDC signed-out title.
- Prevent browsers from using stale OIDC login integration and configuration after a plugin update or settings change.
- Show the OIDC signed-out page when using Jellyfin 12's User Menu logout control.

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
