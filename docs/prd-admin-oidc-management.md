# PRD: Administrator OIDC User Management

Status: Implemented, updated product decisions

## Product-owner refinements

- Use only existing Jellyfin components for the injected tab, form, modal, and notifications.
- Add independent new-user naming options: Identity Provider username (with verified-email fallback) or verified email only. Existing Jellyfin usernames remain unchanged.
- Support unlinking from the signed-in user's own profile, sharing administrator unlink semantics and the configuration page's native confirmation styling. Ordinary users can only unlink themselves.
- The individual-user approval form has a **Match by** select with Subject (`sub`), Email, and Username, one **Value** input, and an **Approve link** button. Exactly one criterion is selected; combined criteria are superseded.
- Username approval matches the configured top-level Identity Provider Username claim case-insensitively. Email requires current verification. Subject remains exact and case-sensitive. Every successful approval creates the durable issuer + `sub` Identity Link.
- Main settings offer **Automatic linking**: No automatic linking, Match email to Jellyfin username (backward-compatible default), or Match Identity Provider username to Jellyfin username. Explicit approvals remain available in every mode.
- Pending approvals keep their original criterion when automatic matching changes. Username approvals record the Username claim used at creation; changing that claim requires editing the approval before fulfilment.

## Goal

Give Jellyfin administrators an `OIDC` tab in the existing user-management editor. Administrators can inspect and remove an Identity Link, sign out the target Jellyfin User's OIDC-origin sessions, and approve linking a selected Jellyfin User on their next Identity Provider sign-in.

Use the terminology in `CONTEXT.md`. An Identity Link is a durable issuer + `sub` binding to a Jellyfin User. Removing a link does not delete or merge Jellyfin Users or transfer their media activity.

## Decisions

### Confirmed by the product owner

- Inject an `OIDC` tab into Dashboard user management using existing Jellyfin components and styling.
- Administrators may unlink another Jellyfin User or themselves.
- Unlinking signs out OIDC-origin Jellyfin sessions, preserving local-password and Quick Connect sessions.
- A confirmation modal explains session termination and local-password consequences. Self-unlinking remains allowed even for the sole administrator; warn rather than require an alternate administrator.
- Administrative unlinking blocks automatic reattachment to that Jellyfin User. A fresh explicit self-service link or a fulfilled administrator-approved link request is required to establish a link again.
- Administrators select subject, email, or Identity Provider username for a selected Jellyfin User to match on next sign-in.
- Email-based matching requires verification in the current OIDC response. Previously retained metadata and administrator-entered email are not proof of verification.
- Pending link requests have no automatic expiry. They remain active until fulfilled, edited, or cancelled by an administrator.

### Explicit proposed defaults

Use these defaults for the draft implementation unless the product owner changes them:

- Sessions issued before OIDC-origin tracking existed are **unclassified**. Preserve them rather than guessing their origin or revoking unrelated sessions. Explain this migration limitation in documentation and the unlink confirmation. This is a known limitation of the requested OIDC-only sign-out guarantee, not a reason to silently sign everyone out.
- Administrator linking is approval for the **next validated sign-in**, not immediate creation of a durable Identity Link from manually entered data.

## Current Implementation

Read these files before implementation:

| File | Relevant responsibility |
| --- | --- |
| `CONTEXT.md` | Domain model, supported claims, password policies, identity resolution, and security boundaries |
| `docs/prd-self-service-identity-provider-linking.md` | Existing self-service flow and profile UI |
| `Jellyfin.Plugin.Oidc/Web/oidc.js` | Browser injection, active-server credentials, profile status, notifications, and password-control visibility |
| `Jellyfin.Plugin.Oidc/WebInjectionStartupFilter.cs` | Web document injection and creation of Jellyfin sessions after OIDC handoff |
| `Jellyfin.Plugin.Oidc/Api/OidcLinkController.cs` | Authenticated self-service status, initiation, and completion |
| `Jellyfin.Plugin.Oidc/Identity/OidcUserProvisioner.cs` | Identity resolution, provisioning, explicit linking, and configuration serialization gate |
| `Jellyfin.Plugin.Oidc/Identity/OidcOptions.cs` | Validated OIDC callback handling and distinction between sign-in and linking |
| `Jellyfin.Plugin.Oidc/Identity/OidcLinkStore.cs` | Single-use, five-minute self-service linking transactions |
| `Jellyfin.Plugin.Oidc/Identity/PasswordLoginEnforcer.cs` | Effective local-password policy |
| `Jellyfin.Plugin.Oidc/Configuration/PluginConfiguration.cs` | Durable plugin state and Identity Links |
| `tests/e2e/specs/oidc.spec.mjs` | Existing browser fixtures and clean-stack flows |

The current plugin does not record reliable OIDC session provenance. User-level password policy is not evidence that a particular session originated through OIDC.

Jellyfin 12's dashboard user editor currently uses `#usersEditPage`, a tab list, and routes shaped like `#/dashboard/users/{userId}/{tab}`. Its native tabs are Profile, Access, Parental Control, and Password. The dashboard is shared by modern and legacy UI. Verify these details against the pinned test image before choosing selectors or route behavior. There is no tab-extension hook in the editor implementation inspected for this PRD.

The current web injection is conditional on OIDC being enabled. Supporting administrative recovery while OIDC is disabled will require separating administrator integration availability from sign-in integration availability. Disabled OIDC must not cause a sign-in button or redirect to appear.

## Scope

- Administrator-only link status and operations for a selected Jellyfin User.
- An injected `OIDC` tab and panel in the existing dashboard user editor.
- Confirmation-based unlinking, including self-unlinking.
- Durable automatic-reattachment suppression for administratively unlinked Jellyfin Users.
- Durable, editable, cancellable administrator-approved link requests with no automatic expiry.
- OIDC-origin session tracking and targeted token revocation.
- Recovery handling for stale links and partially completed session revocation.
- Localization, accessibility, existing themes, path-base deployments, and both supported UI variants.

### Non-goals

- Unlinking another Jellyfin User without administrator authorization.
- Multiple Identity Providers or a provider chooser.
- Direct identity transfer, replacement of a conflicting link, user merging, or movement of media activity.
- Signing out at the Identity Provider itself or revoking its tokens.
- Revoking all Jellyfin sessions as a substitute for OIDC-only revocation.
- Setting or resetting passwords in the OIDC panel. Use Jellyfin's existing Password tab.
- Banning an Identity Provider identity from the Jellyfin server.
- Changes to provisioning usernames, group eligibility, administrator synchronization, or supported OIDC protocol flows beyond integration with pending requests.
- A new frontend framework, a separate administrative settings page, or a redesigned user editor.

## User Interface

### Tab integration

- Add exactly one `OIDC` tab alongside the native user-editor tabs.
- Show it only to authenticated administrators. Backend authorization remains mandatory for every operation.
- Operate on the user selected in the dashboard, not necessarily the administrator's own Jellyfin User.
- Preserve the native tabs and their content. Switching out of OIDC must restore the host editor cleanly.
- Support browser Back/Forward, reload, direct navigation, user switching, and React remounts without duplicate tabs, stale metadata, or actions targeting a previous user.
- Give OIDC selection a local route or query state compatible with the existing editor shell. Adding a DOM tab does not register a React route; do not assume an arbitrary native tab value will work.
- Match Jellyfin's tab styling, selected indication, focus treatment, keyboard navigation, `tab`/`tabpanel` roles, and accessible relationships.
- Reuse the existing compact SSO card's status display, theme colors, and Material icons where appropriate. The administrative tab label remains `OIDC`.

### Panel states

| State | Display | Actions |
| --- | --- | --- |
| Loading | Loading feedback, no assumed link state | Mutations disabled |
| Not linked | `Not linked`; explain any automatic-reattachment block | `Link on next sign-in` |
| Pending request | `Waiting for Identity Provider sign-in` and expected email/subject | `Edit request`, `Cancel request` |
| Linked | Verified email first, retained username second, otherwise `Linked` | `Unlink account` |
| Unlinked with incomplete revocation | Unlinked status and a persistent partial-failure message | `Retry sign-out`; other actions must respect current versions |
| Status unavailable | Error feedback, not `Not linked` | `Retry` |
| OIDC disabled or configuration invalid | Existing link and recovery status remain accessible; explain that new OIDC linking is unavailable | Unlink/retry available; creating a new request disabled |

Do not present self-service `Link account` navigation as administrator linking. The administrator must not authenticate at the Identity Provider on the selected user's behalf.

### Link-request form

- Fields: `Match by` select (Subject, Email, Username) and one `Value` input.
- Require exactly one non-empty criterion.
- Explain that email must be verified by the Identity Provider at sign-in; subject is an exact, case-sensitive identifier supplied by that Identity Provider.
- Derive the issuer server-side from the configured Identity Provider. Do not add an issuer input or endpoint overrides.
- Validate email input syntactically, trim surrounding form-input whitespace, and preserve subject case. Match email case-insensitively without provider-specific alias normalization.
- Show the selected Jellyfin User clearly. Username approval uses the configured Username claim, case-insensitively, and does not require it to equal the Jellyfin username.
- Use `Approve link` and `Cancel`. Approval does not immediately create an Identity Link or change password availability.
- Explain that the next eligible matching sign-in will use this existing Jellyfin User rather than provision another one. The request remains pending until fulfilled or explicitly changed/cancelled.
- Editing replaces the approval and invalidates its previous version. Cancellation invalidates the approval immediately.

### Unlink confirmation

Use Jellyfin's existing modal/dialog pattern with `Cancel` and destructive `Unlink account` actions. Name the selected Jellyfin User and explain:

- The Identity Link will be removed and positively identified OIDC-origin Jellyfin sessions will be signed out.
- Local-password and Quick Connect sessions remain active.
- The current administrator session will end if it belongs to the target user and originated through OIDC.
- Automatic reattachment to this Jellyfin User is blocked until a fresh explicit link or administrator-approved request succeeds.

Add the applicable password-policy explanation:

| Mode | Confirmation consequence |
| --- | --- |
| `AllowForAllUsers` | Local-password sign-in remains available; a known local password is needed. |
| `DisableForLinkedUsersOnly` | Removing the final Identity Link restores local-password sign-in; it does not create or reveal a password. |
| `DisableForAllUsers` | Local-password sign-in remains disabled. The user may need an administrator-approved link to regain access. |

For self-unlinking, prominently warn about losing access when no known local password is available or passwords remain globally disabled. If the target is the sole administrator, warn that recovery may require host-level access. **Do not block the action or require another administrator**, provided the normal confirmation and administrator authorization are satisfied.

Document and explain that older unclassified sessions cannot be selectively identified. Never imply that every historical OIDC session was revoked when that cannot be established.

### Feedback

- Successful unlinking refreshes authoritative state to `Not linked` and explains the reattachment block.
- Successful request creation refreshes to Pending; fulfilment refreshes to Linked when the panel next loads.
- Use existing Jellyfin notifications for short feedback. Ordinary success/failure toasts expire after five seconds, consistent with self-service linking.
- Keep actionable partial-revocation failures visible in the panel until resolved. Do not rely on a transient toast for this state.
- If the initiating administrator's current OIDC session is revoked, return that browser to sign-in without an automatic sign-in loop. A preserved local session stays in the editor.

## Domain and Persistence Requirements

Keep these concepts distinct even if storage shares the existing plugin configuration:

1. **Identity Link**: Existing authoritative issuer + subject binding.
2. **Pending Link Request**: Administrator approval for a selected Jellyfin User, scoped to an issuer, with one expected subject, verified email, or username, a creation time, and an opaque version. Username criteria also retain their configured claim name. It has no automatic expiry.
3. **Automatic Reattachment Block**: Durable suppression of automatic identity attachment to an administratively unlinked Jellyfin User.
4. **OIDC Session Provenance**: Durable evidence that a particular Jellyfin authentication token/session was created through OIDC, associated with its Jellyfin User.

Prefer existing storage and serialization patterns. Do not introduce a new database or abstraction layer unless existing facilities cannot satisfy the requirements.

- Permit at most one active Pending Link Request per Jellyfin User.
- Reject duplicate pending subject, email, or username reservations for the same issuer across different Jellyfin Users. Different criterion types matching the same authenticated identity must deny ambiguous resolution.
- Reject requests for a user who already has an Identity Link or is disabled/deleted. A reserved subject already linked to another user cannot be approved.
- Active requests continue to reserve their matching criteria until fulfilled, edited, or cancelled. Do not add a request TTL or renewal flow.
- Keep durable links authoritative. A pending request must never transfer an identity already linked to another Jellyfin User.
- Removing a link also cancels pending requests for that user and invalidates earlier linking attempts.
- Cancelling a request does not clear an Automatic Reattachment Block.
- Creating a request does not clear that block. Only successful fresh explicit linking or fulfilment of the request clears it.
- Disabled/deleted users, stale configuration, and request conflicts must be rechecked at commit time.

## Next-Sign-In Resolution

Extend the shared identity-resolution path, not just a particular UI caller:

1. Validate the OIDC response, issuer, non-empty subject, Allowed Group membership, and current `RequireVerifiedEmail` setting using existing rules.
2. Resolve an existing durable Identity Link first. Pending approvals cannot reassign an already linked identity.
3. Otherwise evaluate active Pending Link Requests for the authenticated issuer. Subject is exact and case-sensitive. Email matches only a supplied currently verified email, case-insensitively. Username matches the configured top-level username claim case-insensitively and requires the approval's recorded claim to remain configured. More than one matching request denies resolution.
4. If a matching request has a disabled/deleted target, was changed/cancelled, or conflicts with a link or another request, deny the operation. Do not fall through to provisioning another user.
5. If one valid request matches, attach the identity to the selected existing Jellyfin User, consume the request, and clear their reattachment block as one serialized operation. Preserve their username and media activity. Apply normal sign-in administrator-group synchronization, password policy, and optional profile-image synchronization.
6. Otherwise apply the configured automatic matching mode, then provisioning rules, subject to reattachment suppression and pending reservations. No automatic linking disables only attachment to existing users.

Additional constraints:

- Automatic Email Match and opt-in Username Match must not attach to a blocked Jellyfin User or bypass an active approval for a different identity.
- A reserved email without current verification cannot be bypassed through automatic matching or fallback provisioning.
- Username collisions remain denials during provisioning. Attachment by username requires the explicit automatic matching mode or a matching administrator approval.
- Fresh authenticated self-service linking remains possible for the initiating user, but must respect reservations and existing link conflicts. Successful explicit linking consumes any compatible request and clears the block; it cannot bypass an incompatible administrator approval.
- Existing sign-in requirements are not waived by an administrator request. Email-based requests always require current verified email, even when `RequireVerifiedEmail` is off.

## Session Revocation

- Record provenance when `WebInjectionStartupFilter` creates a Jellyfin session after an approved OIDC sign-in handoff. A pending-request sign-in produces an OIDC-origin session too.
- Preserve local provenance when self-service linking occurs inside an existing local session. Identity Provider authentication during linking does not replace the Jellyfin session.
- Use stable server-owned authentication record identifiers, or an equivalent supported mechanism, that survives restart and permits targeted revocation. Inspect Jellyfin 12's token/session APIs before choosing storage fields.
- Never classify origin using administrator status, group membership, username, password policy, device-name heuristics, or the mere existence of an Identity Link.
- Revoke underlying authentication tokens so subsequent authenticated requests fail. Merely deleting a visible SessionInfo entry is insufficient.
- Revoke every positively tracked OIDC token for the selected Jellyfin User, including other browsers/devices, while preserving other users' tokens and the target's local/Quick Connect tokens.
- Remove obsolete provenance entries after tokens expire or are revoked using existing lifecycle facilities where possible.
- Preserve unclassified historical sessions under the proposed migration policy. Do not claim Identity Provider session logout or guaranteed immediate cancellation of buffered media playback.

## Concurrency and Failure Handling

- Serialize link/request/block persistence with existing provisioning, explicit linking, and administrator configuration saves using the existing configuration gate.
- Administrative unlinking and request cancellation/replacement must invalidate older callbacks that could attach to the selected Jellyfin User. A configuration-reference check alone does not invalidate a cancelled request or an unlinked identity.
- A callback that started before unlinking cannot restore the removed association, even if its original local Jellyfin session remains valid. Fresh explicit linking started after unlinking is permitted.
- Stale administrator tabs must not remove a replacement link or overwrite a newer request. Use a server-issued version/generation and return conflict feedback that requires refreshing state.
- Unlinking and revocation retries must be idempotent. Duplicate requests must not affect a replacement link or newly established sessions from a later linking generation.
- Do not report unlink success before link removal, suppression state, and password-policy changes are persisted.
- Persist the unlink before targeted session revocation. If revocation partially fails, do not silently restore the link or report complete success. Preserve enough state to retry revocation of the tokens selected by that unlink operation, including after restart.
- A retry for an older unlink operation must not revoke sessions established by a later approved link.
- Failed persistence must not leave a reported successful operation or silently bypass password-policy enforcement. Follow the existing rollback approach where applicable.

Unlinking blocks automatic reattachment to the original Jellyfin User; **it is not an identity-wide access ban**. An eligible identity may still provision a different Jellyfin User when ordinary provisioning rules permit it. Document this distinction and do not add a provider-wide denylist as part of this feature.

## Backend Interface

Suggested route layout, adapt naming to repository conventions while preserving semantics:

| Operation | Suggested endpoint | Requirements |
| --- | --- | --- |
| Inspect selected user | `GET /oidc/admin/users/{userId}` | Link display metadata, approval state, block state, effective password policy, and opaque versions; private and non-cacheable |
| Approve/edit next sign-in | `PUT /oidc/admin/users/{userId}/link-request` | Exactly one expected subject/email/username and prior version; issuer set server-side; no expiry |
| Cancel approval | `DELETE /oidc/admin/users/{userId}/link-request` | Version-checked, idempotent cancellation |
| Unlink | `DELETE /oidc/admin/users/{userId}/link` | Version-checked unlink, suppression, policy application, and OIDC-only revocation |
| Retry incomplete revocation | `POST /oidc/admin/users/{userId}/retry-sign-out` | Refers to the outstanding unlink operation; must not target newer sessions |

- Enforce Jellyfin administrator authorization and the same elevation requirements as native administrative user-edit operations. A valid ordinary-user token is not sufficient.
- Use the active server's authenticated API client. Do not assume the first saved server is active.
- Derive actor identity from authentication. Validate the selected target user server-side; browser route/user IDs are not authorization.
- Use authenticated header-based mutations and existing CSRF protections. Do not put access tokens in navigation URLs.
- Allow inspecting and unlinking existing links while OIDC is disabled or configuration is invalid, provided the plugin is installed and administrator authentication works. Creating new requests requires valid enabled Identity Provider configuration.
- Allow administrators to inspect/recover a link from a previously configured issuer. Do not limit recovery to the current issuer's links.
- Expose no client secrets, access tokens, authorization codes, or complete ID tokens. Expected subject is administrator-only form data, not an anonymous browser setting.
- Log the actor and target Jellyfin User IDs and operation outcome as appropriate, without email, subject, username claims, or authentication tokens.

## Acceptance Criteria

1. Both modern and legacy UI expose exactly one administrator-only OIDC tab in the selected user editor; native tabs, keyboard navigation, browser history, and user switching continue to work.
2. Ordinary users and anonymous callers cannot inspect another user's OIDC metadata, create/cancel approvals, unlink, or revoke sessions through these endpoints.
3. Linked, unlinked, pending, unavailable, and partial-revocation states are distinguishable. Unknown status never appears as unlinked. Pending requests survive restarts and remain active without an expiry until fulfilled, edited, or cancelled.
4. Unlinking preserves the Jellyfin User, username, permissions, administrator status, profile image, and media activity, while removing the link and persisting the reattachment block.
5. Unlinking revokes all positively tracked OIDC-origin tokens for that user and preserves their local/Quick Connect tokens and other users' sessions. Provenance and outstanding revocation survive restart.
6. Self-unlinking, including by the sole administrator, uses the warning modal. An OIDC-origin initiating session ends; a local initiating session remains active. Globally disabled passwords remain disabled.
7. Linked-only password policy is lifted after the final link is removed, with profile password controls restored subject to Jellyfin's own permissions. No password is generated or disclosed by unlinking.
8. Routine Email Match cannot recreate a blocked association. A fresh explicit self-service link or fulfilled approval can re-establish it and clear suppression.
9. Subject, email, and username approvals link the next eligible identity to the selected existing Jellyfin User. Email requests reject absent/unverified email and cannot use retained metadata. The form has one selector, one input, and an Approve link button.
10. Failed eligibility, disabled/deleted targets, ambiguous matches, conflicting links/reservations, cancelled/replaced requests, and old callbacks cannot create an unintended link or bypass the target through fallback provisioning.
11. Existing durable links remain authoritative; no operation merges or transfers users. Username attachment requires an explicit approval or the selected automatic username-matching mode. Normal Administrator Group synchronization still occurs at sign-in.
12. Concurrent operations and stale tabs cannot overwrite newer approvals, remove replacement links, or revoke newer sessions when retrying an old unlink.
13. Unlink recovery works with OIDC disabled, invalid configuration, and links from a previous issuer; disabled OIDC does not introduce login buttons or redirect loops.
14. Localization, keyboard/screen-reader interaction, narrow screens, inherited themes, and path-base deployments work. Modal focus is managed correctly and returns to a sensible control when the session survives.
15. Failure feedback is accurate, retries are safe, and partial revocation remains visible rather than disappearing with a toast.

## Verification and Agent Completion Checklist

1. Read `AGENTS.md`, `CONTEXT.md`, this PRD, and the current self-service implementation. Confirm the proposed defaults above in the implementation summary.
2. Inspect the pinned Jellyfin 12 dashboard markup, native administrator authorization policy, and targeted authentication-token revocation APIs. Do not implement revocation through an all-user-token shortcut.
3. Implement the smallest durable state and shared identity-resolution changes needed for requests, suppression, callback invalidation, and session provenance.
4. Add administrator endpoints and the injected tab using existing browser integration and native styling patterns.
5. Add unit coverage for request matching, absence of automatic expiry, verified-email handling, conflicts, suppression, generations, concurrency, and failure recovery. Exercise the actual shared resolution path rather than only helper functions.
6. Add clean-state e2e coverage for both UI variants: administrator unlink, local versus OIDC versus Quick Connect session preservation/revocation, self-unlink, password-policy outcomes, each approval type, automatic-reattachment suppression, stale callbacks/tabs, authorization denial, disabled-plugin recovery, and restart persistence. Include meaningful failure cases.
7. Update `CONTEXT.md` and `README.md` with the shipped administration flow, exact matching precedence, suppression semantics, persistent requests without expiry, session-provenance migration limitation, self-unlink warning, and recovery behavior. Update the self-service PRD where cross-feature behavior changes.
8. Run `make format`, `make check`, `make test-unit`, and `make test-e2e`. Use `make test-e2e` for a clean Docker state, not direct Playwright execution as a replacement.
9. Review the complete diff for unintended configuration replacement, secrets/identity data in responses or logs, browser regressions, and unsupported protocol changes. Do not commit artifacts, TLS files, build outputs, or reference checkouts.
10. Report implementation details, tests run, and the explicit migration/default choices. Follow repository version/changelog rules when releasing; this PRD does not itself request a release, commit, or push.
