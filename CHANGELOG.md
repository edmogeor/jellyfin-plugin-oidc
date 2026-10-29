## [0.3.5.0]

### Changed

- Update the bundled Microsoft.AspNetCore.Authentication.OpenIdConnect dependency from 10.0.12 to 10.0.13.
- Update development and test dependencies and the CodeQL workflow.

## [0.3.4.0]

### Changed

- Remove redundant username-matching help text from the OIDC configuration page and all supported translations.

## [0.3.3.0]

### Changed

- Align the OIDC configuration page width and spacing with Jellyfin's native settings layout, and remove redundant password-policy help text.
- Simplify OIDC sign-in, linking, logout, and profile-image flows, share browser session helpers and local-password policy checks, and streamline service constructors.

## [0.3.2.0]

### Changed

- Replace build-specific webpack hooks with page IDs and semantic controls for the administrator OIDC tab, preserving approvals, unlinking, recovery, and native navigation.
- Anchor the Provider profile card to existing read-only content rather than padding classes.

### Fixed

- Load settings when returning to the configuration page without a browser refresh, and prevent detached pages from updating the current form.
- Center the sign-in icon and label using a full-width flex layout.
- Restore native tabs' accessible selection state when leaving OIDC and keep the selected OIDC tab visible on narrow screens.
- Wait for the login view before displaying sign-in errors so notifications survive initial page rendering.

## [0.3.1.0]

### Changed

- Remove the direct sign-in redirection shortcut from General settings; the setting remains in Advanced.
- Clarify that automatic linking applies to Jellyfin Users who already exist in all supported translations.

### Fixed

- Hide Remember Me on the OIDC-only signed-out page.
- Show Jellyfin's loading spinner while installing an OIDC session, without flashing provider sign-in controls before the signed-in page.

## [0.3.0.0]

### Added

- Self-service Identity Provider linking and unlinking from the signed-in Jellyfin User's profile, with an administrator-controlled availability setting.
- An OIDC tab in administrator user settings for inspecting Identity Links, approving the next sign-in by subject, verified email, or username, unlinking, and retrying incomplete OIDC session revocation.
- Configurable top-level username claims, automatic username matching, new-user username selection, and support for email-free identities when the verified-email requirement is disabled.
- A Provider selector for Authentik, Authelia, Keycloak, Pocket ID, ZITADEL, and Other, with locally bundled monochrome logos across the sign-in button, user profile, and administrator status row.
- An optional display name for Other providers, with a key icon and SSO fallback.

### Changed

- Reorganize the localized configuration page into General and Advanced tabs, with keyboard navigation and validation that reveals the relevant field.
- Disable automatic linking and self-service linking by default for new installations while preserving existing saved behavior.
- Hide profile password controls when local-password sign-in is disabled for that Jellyfin User.
- Respect Remember Me and the requested local return route during OIDC sign-in, and preserve other Jellyfin servers' saved credentials.
- Show provider identity before administrator link status with a subtle separator, and visibly grey out approval controls for empty or invalid values.
- Clarify that the embedded administrator integration is verified against Jellyfin Web 12.0.0; incompatible editor module APIs preserve Jellyfin's native editor.

### Removed

- Replace free-form login-button text with localized Sign In with Provider Name wording and the optional Other provider name. Previously saved custom button text is no longer used.

### Fixed

- Prevent automatic sign-in loops after logout fallbacks, including missing logout cookies and unsupported Identity Provider logout.
- Complete local browser sign-out when server logout cannot be confirmed, display a warning, and target only the active server's credentials.
- Support large protected ID tokens using chunked logout cookies.
- Remove only the Jellyfin User created by a failed provisioning attempt when its Identity Link cannot be persisted, allowing a clean retry.
- Reject unusable group lists, empty group claims, unsupported configuration values, and issuer or public URLs containing credentials, queries, or fragments.
- Preserve configuration-page translations behind a Jellyfin path base and retain embedded English when browser translation requests fail.

### Security

- Bind single-use session handoff tickets to an HttpOnly, Secure browser nonce and make credential-bearing index responses private and non-cacheable.
- Persist exact OIDC session provenance for targeted unlink revocation while preserving local-password, Quick Connect, and unclassified historical sessions.
- Invalidate stale callbacks and handoff tickets after administrator identity-management operations, and reject stale approval or unlink edits using per-user versions.
- Enforce globally disabled passwords for newly created Jellyfin Users, reconcile enabled configuration at startup, and restore previous authentication providers after failed configuration updates.
- Apply the profile-image download deadline to connection, headers, and body reads so a stalled response cannot hold the shared configuration gate indefinitely.

## [0.2.5.0]

### Security

- Persist provisioned Jellyfin Users with their random password and local-password policy in the initial database insert, preventing passwordless access during provisioning or after a partial failure.
- Reject automatic Email Match for Jellyfin Users already owned by another Identity Link, while preserving automatic matching for unlinked users.
- Serialize provisioning and administrator configuration saves, reject stale callbacks, and prevent sign-in from reverting administrator access restrictions.

### Fixed

- Prevent delayed configuration responses from replacing saved settings or unsaved edits in the configuration page.
- Track submitted settings during saves and show save failures while retaining the draft for retry.

## [0.2.4.0]

### Fixed

- Show English configuration text immediately from the embedded page while other translations load.

## [0.2.3.0]

### Changed

- Refresh the OIDC configuration page layout and spacing.

### Fixed

- Load configuration-page translations after temporary failures, so settings labels appear without reopening the page.

## [0.2.2.0]

### Changed

- Show the default OIDC sign-in label as a placeholder in the configuration form.

## [0.2.1.0]

### Fixed

- Keep OIDC-only sign-in within Jellyfin's native sign-in form.

## [0.2.0.0]

### Changed

- Simplify OIDC configuration and browser login integration without changing behavior.

## [0.1.28.0]

### Fixed

- Keep themed OIDC-only login controls consistent while Jellyfin rebuilds its login page.

## [0.1.27.0]

### Fixed

- Keep the OIDC sign-in action primary and preserve themed OIDC-only login controls after sign-in errors.

## [0.1.26.0]

### Fixed

- Publish the release manifest with its generated changelog.

## [0.1.25.0]

### Fixed

- Prevent direct Identity Provider redirects when password login remains available.

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
