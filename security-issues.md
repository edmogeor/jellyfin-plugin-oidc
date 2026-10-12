# Security Audit: Unauthorized Access

## Summary

Three plugin-controlled access risks were identified and fixed. The findings below describe the pre-fix behavior; their source line references refer to the audited implementation.

## Resolution

1. Provisioning inserts the random password and configured local authentication provider together with the Jellyfin User, using Jellyfin's database context and default permissions/preferences. Hashing or database-save failures cannot leave a passwordless provisioned user.
2. Automatic Email Match remains available for unlinked Jellyfin Users. A match already owned by another Identity Link is denied.
3. Provisioning and administrator configuration saves share a synchronization gate. Callbacks authenticated with a replaced configuration are denied, and sign-in saves Identity Link changes without replacing configuration or reverting administrator settings.

Regression coverage includes initial password policy for all three modes, hashing and database-save failures, first-time Email Match, rejection of another subject, concurrent configuration updates, and stale callbacks. Browser coverage checks empty-password rejection and denial of matching a Jellyfin User linked to another subject.

### Verification after fixes

- `make check` passed.
- `make test-unit` passed: 44 tests.
- `make test-e2e` passed from a clean Docker state: 22 tests, including configuration UI regressions.

## 1. High: Provisioning exposes a passwordless Jellyfin User

**Location:** `Jellyfin.Plugin.Oidc/Identity/OidcUserProvisioner.cs:66-68`, password policy applied at `:103`.

The plugin creates and persists the Jellyfin User first, then generates and saves its random password. Jellyfin creates that user with the default local authentication provider and no password.

### Unauthorized-access path

- An attacker who knows the email can attempt local authentication with an empty password during that interval.
- If hashing or the subsequent user update fails, the passwordless user remains.
- Selecting `DisableForAllUsers` does not protect this interval because enforcement happens later.

### Validation

An isolated check using the real provisioner and an injected hashing failure left the created user passwordless with the default authentication provider. Jellyfin's default provider accepts empty-password authentication for passwordless users. The concurrent HTTP race was not exercised.

### Recommended fix

Make provisioning fail closed. The Jellyfin User must be persisted with a random password or a rejecting authentication provider from creation. Generating the hash earlier alone does not eliminate the interval between creation and the security update.

## 2. High: Email Match can grant access to an already-linked Jellyfin User

**Location:** `Jellyfin.Plugin.Oidc/Identity/OidcUserProvisioner.cs:42`, `:54-63`, `:89-92`.

When the incoming subject has no Identity Link, the plugin accepts an Email Match without checking whether that Jellyfin User already belongs to another Identity Link.

### Unauthorized-access path

1. A Jellyfin User is linked to subject A.
2. Another eligible identity, subject B, obtains the same verified email, for example through mailbox reassignment or identity deletion and recreation.
3. Subject B signs in.
4. The plugin returns A's Jellyfin User and creates a second Identity Link to it.

This requires a verified email and Allowed Group membership. It does not require duplicate emails simultaneously at the Identity Provider. Administrator status is resynchronized, so this does not automatically preserve the victim's administrator role, but it grants access to the victim's Jellyfin User and its retained permissions and data.

### Validation

The isolated check returned the existing Jellyfin User ID to a different subject and created a second Identity Link.

### Recommended fix

Allow automatic Email Match only for Jellyfin Users without an existing Identity Link. Require an explicit administrator-controlled reassignment for already-linked users.

## 3. High: An in-flight sign-in can undo administrator access revocation

**Location:** `Jellyfin.Plugin.Oidc/Identity/OidcUserProvisioner.cs:31`, `:115`; `Jellyfin.Plugin.Oidc/OidcPlugin.cs:44-62`.

Provisioning captures the configuration object at the start, then later saves that entire object through `UpdateConfiguration`. Administrator changes use a separate configuration object and do not share the provisioner's synchronization.

### Unauthorized-access path

1. An eligible sign-in starts.
2. An administrator disables OIDC or tightens the Allowed Groups.
3. The pending sign-in finishes and saves its older configuration.
4. The administrator's restriction is overwritten, permitting subsequent sign-ins under the old settings.

Optional profile-image synchronization introduces additional network waits before that stale save.

### Validation

Disabling OIDC during the isolated provisioner check was overwritten when provisioning completed, restoring `Enabled = true`.

### Recommended fix

Synchronize administrator updates with Identity Link persistence and apply link changes to the latest configuration. Never resave an old configuration snapshot wholesale. Revalidate eligibility when relevant settings change before issuing the handoff ticket.

## Checks and scope

- 36/36 existing unit tests passed.
- Three additional isolated checks reproduced the findings using the real provisioner with mocked Jellyfin services.
- Reviewed claim/group validation, OIDC handling, Identity Links, password enforcement, and session handoff.
- Jellyfin's API layer enforces disabled-user and remote-access restrictions, so direct session creation was not counted as bypassing those controls.
- Existing-session retention after group removal is documented behavior, not a finding here.
- The original audit made no repository changes and did not modify live users. The fixes and regression coverage are recorded in the Resolution section above.
