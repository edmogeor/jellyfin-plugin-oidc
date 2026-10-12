# PRD: Self-Service Identity Provider Linking

Status: Implemented

## Problem

A Jellyfin User with an existing local sign-in needs a way to create an Identity Link to the configured Identity Provider without administrator intervention or an automatic Email Match. The Jellyfin User must retain their permissions, media activity, and user ID.

## Goal

Let an authenticated Jellyfin User link their own profile to the single configured Identity Provider through a small section injected into Jellyfin Web's user profile page.

The interface must work in both modern UI and legacy UI and look like part of Jellyfin. Its resting state consists of a linked status with verified email when recorded, otherwise the Identity Provider username when recorded, and, when unlinked, one button to link. Preserve existing Jellyfin usernames.

## Scope

- Inject into the signed-in Jellyfin User's own profile page.
- Show whether that Jellyfin User has an Identity Link to the configured Identity Provider.
- Start an explicit linking flow from a `Link account` button.
- Authenticate with the Identity Provider and persist an Identity Link to the initiating Jellyfin User.
- Return to the same profile page with updated status or actionable failure feedback.
- Reuse the existing browser injection, OIDC validation, Identity Link storage, and configuration serialization boundaries.

### Non-goals

- Self-service unlinking, replacement of an existing Identity Link, or identity transfer.
- Linking another Jellyfin User through an administrator's profile editor.
- Multiple Identity Providers or a provider chooser.
- Provisioning, merging Jellyfin Users, or moving media activity during linking.
- A separate settings page, custom modal, custom component library, or redesigned profile page.
- Native Jellyfin client integration.

## User Experience

### Placement and visual integration

Inject one compact `SSO` card below the profile image and username and above the password fields when those fields are available. Use Jellyfin's `paperList` and `listItemBody` classes for a non-interactive surface with no hover effect. Use even `1em` padding, a small semibold title in the primary text color, and a status line beneath it. Place the natural-width `Link account` button with a leading Jellyfin link icon on the right, vertically centered with the text; wrap on narrow screens. Do not overlay or replace existing profile controls.

Use Jellyfin's existing components and classes for headings, descriptive text, buttons, loading feedback, and notifications. The existing injection uses `emby-button` and Jellyfin button classes; treat that as an incumbent pattern, not proof that the same markup supports both UI variants. Verify each variant's actual profile structure and component initialization before choosing integration points.

Use a shared linking behavior with only the minimum variant-specific insertion or component adaptation needed. Do not add a framework dependency, duplicate the flow, hard-code theme colors, or recreate Jellyfin styling. Support inherited themes, narrow screens, keyboard navigation, and visible focus states.

### States and copy

| State | Status | Action |
| --- | --- | --- |
| Loading | `Checking link status...` | No enabled link button |
| Unlinked | `Not linked`, red cross | `Link account` |
| Starting | `Not linked` | Disabled button labeled `Redirecting...` |
| Linked with recorded verified email | `Linked as user@example.com`, green tick | No link button |
| Linked without email, with recorded username | `Linked as provider-user`, green tick | No link button |
| Linked with neither recorded value | `Linked`, green tick | No link button |
| Status unavailable | `Unable to check link status.` | Retry through Jellyfin's existing feedback/action pattern |
| Plugin disabled or not configured | Section hidden | None |

The linked state is informational, with no unlink action. Display retained verified email first, then the retained Identity Provider username from the configured Username claim. Do not substitute the Jellyfin username. Render as plain text, wrap long values, and do not expose issuer, subject, or tokens. Unknown or failed status must never be displayed as `Not linked`.

Use Jellyfin's Material icons and theme error/success colors alongside the text, never color alone. Hide decorative icons from screen readers, announce asynchronous status changes accessibly, and localize user-facing copy through the plugin's existing translation mechanism. Loading and status failures remain neutral rather than showing a linked/unlinked icon.

### Happy path

1. A Jellyfin User opens their own profile page while signed in.
2. The section loads their current Identity Link status from an authenticated endpoint.
3. If unlinked, they select `Link account`.
4. The plugin starts a linking-specific OIDC transaction bound to that authenticated Jellyfin User and returns the browser to the configured Identity Provider.
5. The Identity Provider authenticates the identity using the existing authorization-code flow.
6. The plugin validates the response and stages a single-use linking completion ticket.
7. The browser returns to the originating profile route, retaining the current Jellyfin session, and submits an authenticated completion request. The server verifies the initiating session, rechecks eligibility and conflicts, and persists the Identity Link.
8. Once persistence succeeds, the section refreshes to `Linked`. Show a brief success notification using Jellyfin's existing notification pattern.

### Failure and cancellation

Cancellation, expired transactions, invalid responses, configuration changes, ineligible identities, and conflicts create no new Identity Link and do not switch the signed-in Jellyfin User. Return to the profile page where possible, refresh authoritative status, and show concise feedback with the link button available when still unlinked.

Suggested general failure copy: `We couldn't link your Identity Provider. Please try again.` For conflicts, explain that the identity cannot be linked without identifying the other Jellyfin User. Do not offer an override.

Navigation, profile remounts, back/forward navigation, and switching Jellyfin Users must not produce duplicate sections, stale status, or a link action for the previous user. Do not repeatedly fetch status for unrelated DOM mutations.

## Functional Requirements

### Authenticated ownership

- Status and initiation require a valid Jellyfin session and operate only on its Jellyfin User.
- Derive the target user ID server-side from authentication, never from a trusted browser-supplied user ID.
- Select credentials for the active Jellyfin server rather than assuming the first saved server is active.
- Initiate through an authenticated, CSRF-protected operation as appropriate to Jellyfin's authentication mechanism. Do not put a Jellyfin access token in a navigation URL.
- Bind the callback to the original user and browser transaction with protected OIDC state and correlation validation. A callback must not become a normal sign-in or an anonymous link operation.
- Require the initiating Jellyfin session to remain valid and the target Jellyfin User to remain eligible at completion. If the browser is now signed in as another user, reject completion.

### Identity validation and persistence

- Preserve existing discovery, confidential authorization-code flow, PKCE, HTTPS, and token-validation requirements.
- Require a non-empty `sub` and membership in an Allowed Group. The administrator's default-enabled `Require verified email` setting additionally requires a supplied non-empty email with `email_verified=true` for linking and all Identity Provider sign-ins, including linked users. Disabling it allows identities without email. Automatic Email Match always requires a supplied verified email.
- Bind the validated issuer and subject to the initiating Jellyfin User directly. An Email Match is not required for explicit linking.
- Issuer and subject remain the authoritative Identity Link key. Retain supplied non-empty verified email and configured username-claim values as display metadata during successful linking and sign-in. Refresh supplied values; absent username or absent/unverified email must not erase retained values. Expose these values only in authenticated profile status, never anonymous browser configuration or logs. Retained email does not satisfy the verified-email requirement for a new authentication response.
- Do not run the ordinary matching/provisioning path to choose a target Jellyfin User.
- Reject an identity linked to a different Jellyfin User and a target user already linked to a different identity. Never overwrite either link.
- Repeated completion for the same user and identity is harmless. Consumed or expired transactions cannot create another binding.
- Serialize conflict checks and persistence with sign-in and administrator configuration changes. Recheck conditions at commit time so concurrent attempts cannot create conflicting links.
- Reject completion if relevant Identity Provider configuration was replaced while the flow was in progress.
- Report success only after the Identity Link and required local-password policy are persisted. Failures must not leave a reported successful but unusable link.

### Existing policies

Agreed behavior:

- Linking itself preserves the Jellyfin User's username, administrator status, other permissions, and profile image.
- Apply the configured local-password policy when the Identity Link is created. Under `DisableForLinkedUsersOnly`, linking disables subsequent local-password sign-in. Show a short explanation before the button when that policy applies: `Linking will disable password sign-in for this Jellyfin User.`
- Hide the entire profile password form, including save/reset buttons, when the authenticated Jellyfin User's effective local-password policy disables password sign-in. This includes all users under `DisableForAllUsers` and users with any Identity Link under `DisableForLinkedUsersOnly`. Preserve password controls for unlinked users in linked-only mode. Refresh visibility after linking and configuration changes; restore controls when passwords are allowed without overriding Jellyfin's permission-based visibility or applying the initiating user's status to another user's profile.
- Preserve the current Jellyfin session; linking does not issue a sign-in handoff or replace browser credentials.
- Subsequent OIDC sign-ins preserve the Jellyfin username, including when email or username claims change. Administrator Group synchronization and optional profile-image synchronization continue as usual.
- Omit the general Identity Provider sign-in explanation from the card. Retain the applicable password-disable notice and conflict feedback.

## Security and Compatibility Boundaries

- Support one Identity Provider and top-level claims only. The new `Username claim` setting defaults to `preferred_username`, accepts an exact top-level string claim name, and supplies new provisioning names and the linked-profile username fallback. Missing username claims fall back to verified email for provisioning. Missing both values or a username collision denies provisioning. Choosing `email` uses only verified email. These naming rules do not affect explicit linking's target or existing Jellyfin usernames.
- Accept only local relative return routes, preserving rejection of absolute, protocol-relative, and backslash paths. Support Jellyfin path-base deployments.
- Keep status responses private and non-cacheable; expose only the minimal link state, recorded verified email and username, and policy settings for the signed-in Jellyfin User. Never expose another Jellyfin User's metadata or issuer/subject identifiers.
- Keep client secrets, access tokens, authorization codes, complete ID tokens, and identity identifiers out of browser configuration responses, user-facing errors, and logs.
- Verify integration against both supported Jellyfin Web UI variants. Profile route names, selectors, and modern component access are implementation discoveries, not assumed contracts.
- When no supported profile insertion point exists, leave the profile intact and avoid injecting controls elsewhere.

## Acceptance Criteria

1. In both modern UI and legacy UI, an authenticated unlinked Jellyfin User sees one native-looking `SSO` card above the password fields with a red cross, `Not linked`, and a working right-aligned `Link account` button. The card has no hover behavior.
2. A linked Jellyfin User sees `Linked as <verified email>`, falling back to `Linked as <Identity Provider username>`, then `Linked`, with no link or unlink action. Disabled or invalid plugin configuration hides the section.
3. Successful linking attaches the authenticated identity to the initiating Jellyfin User without changing their username, provided no Identity Link conflict exists and configured email requirements are satisfied.
4. The user ID, media activity, current session, and permissions are preserved at linking. The configured local-password policy takes effect, with applicable consequences explained before initiation.
5. The next OIDC sign-in resolves the new Identity Link to the same Jellyfin User and preserves their username. Default-enabled verified-email enforcement applies at every sign-in; disabling it permits email-free identities. Supplied metadata updates the profile display.
6. Cancellation, failure, expired or replayed transactions, revoked sessions, user switching, and replaced configuration cannot create an unintended Identity Link or sign in a different Jellyfin User.
7. Existing Identity Links cannot be overwritten, including under concurrent linking or sign-in requests.
8. The section behaves correctly after repeated navigation, remounting, reload, and switching users or servers, without duplication or stale status.
9. Keyboard and screen-reader use, localization, inherited themes, narrow screens, and path-base deployments are supported in both UI variants.
10. Unit coverage verifies ownership, eligibility, conflict handling, transaction lifetime, and policy application. End-to-end coverage exercises success and failure in both UI variants. Implementation passes `make check`, `make test-unit`, and `make test-e2e`.

## Documentation and Release

`CONTEXT.md` documents explicit linking, its ownership boundary, policy effects, and differences from sign-in.

User documentation must include the profile entry point and recovery path for conflicts, which still requires an administrator to clear an existing Identity Link. Follow the repository's version and changelog requirements when releasing.
