<div align="center">
  <img src="assets/thumb.png" width="480" alt="OIDC Authentication" />
  <h1>OIDC Authentication for Jellyfin</h1>
  <p>
    <a href="https://github.com/edmogeor/jellyfin-plugin-oidc/actions/workflows/ci.yml">
      <img src="https://github.com/edmogeor/jellyfin-plugin-oidc/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" />
    </a>
    <a href="https://github.com/edmogeor/jellyfin-plugin-oidc/releases">
      <img src="https://img.shields.io/github/v/release/edmogeor/jellyfin-plugin-oidc" alt="Latest release" />
    </a>
    <a href="LICENSE">
      <img src="https://img.shields.io/badge/License-GPL--3.0--or--later-blue.svg" alt="License: GPL-3.0-or-later" />
    </a>
  </p>
</div>

Simple OpenID Connect sign-in for Jellyfin 12+, with one Identity Provider, group-based access, and Jellyfin-controlled permissions and viewing activity.

<!-- toc -->

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Provider Setup](#provider-setup)
- [Sign In](#sign-in)
- [Link Your Identity Provider](#link-your-identity-provider)
- [Administrator User Management](#administrator-user-management)
- [Password Login](#password-login)
- [Custom Themes](#custom-themes)
- [Logout](#logout)
- [Development](#development)
- [Donations](#donations)
- [License](#license)

<!-- tocstop -->

## Features

- Sign in with an OIDC service that supports standard discovery and can return verified email and flat group claims
- Show a configurable sign-in button, with an optional direct redirect to the sign-in service when local passwords are off
- Allow access and administrator rights based on groups
- Create Jellyfin users when eligible users sign in for the first time
- Choose automatic linking by verified email or Identity Provider username, or require explicit linking
- Link an existing Jellyfin User from their profile in modern or legacy UI without changing their username
- Sign out from both Jellyfin and the sign-in service when supported
- Localized OIDC configuration page: Danish, German, English (US), Spanish, Finnish, French, Italian, Japanese, Korean, Norwegian Bokmal, Dutch, Polish, Portuguese (Brazil), Russian, Swedish, and Chinese (Simplified)

## Prerequisites

- Jellyfin 12 or later
- An OIDC service with a configured client ID, client secret, and callback URL. See [Provider Setup](#provider-setup).
- At least one allowed or administrator group configured in the plugin.
- For automatic email matching, existing Jellyfin usernames must match verified OIDC email case-insensitively. Optional username matching uses the configured **Username claim** instead. Alternatively, use self-service linking or an administrator approval. Email is optional when **Require verified email** is disabled.
- An administrator group before disabling local passwords for every Jellyfin user. Test the OIDC flow in a separate browser session before signing out.

## Quick Start

1. In **Dashboard > Plugins > Repositories**, add this repository URL:

   ```text
   https://raw.githubusercontent.com/edmogeor/jellyfin-plugin-oidc/manifest-release/manifest.json
   ```

2. Install the plugin in Jellyfin 12, then restart Jellyfin.
3. In your OIDC service, create a confidential client. Add this callback URL:

   ```text
   https://jellyfin.example.com/oidc/callback
   ```

4. Open **Dashboard > Plugins > OIDC Authentication**. Enter the issuer URL, client ID, client secret, and at least one allowed or administrator group.
5. Turn on OIDC and save your changes.

OIDC sign-in requires an HTTPS public URL. If Jellyfin is behind a reverse proxy or has more than one public address, set **Public Jellyfin URL override** to its public HTTPS address. Otherwise, the plugin uses the HTTPS address in the browser.

## Configuration

| Setting                      | Required                   | Default             | Description                                                                                   |
| ---------------------------- | -------------------------- | ------------------- | --------------------------------------------------------------------------------------------- |
| Enable OIDC                  | Yes                        | Off                 | Lets users who meet your group rules sign in.                                                 |
| Public Jellyfin URL override | No                         | Browser HTTPS address | The public HTTPS address to use for callbacks and sign-out.                                 |
| Issuer URL                   | Yes                        | -                   | The HTTPS address of your OIDC service.                                                       |
| Client ID                    | Yes                        | -                   | The client ID from your OIDC service.                                                         |
| Client secret                | Yes                        | -                   | The client secret from your OIDC service.                                                     |
| Allowed groups               | At least one group setting | -                   | Comma-separated groups that can sign in. New Jellyfin users can be created for their members. |
| Administrator groups         | At least one group setting | -                   | Comma-separated groups that can sign in as Jellyfin administrators.                           |
| Group claim                  | No                         | `groups`            | The top-level claim that lists a user's groups.                                               |
| Username claim               | Yes                        | `preferred_username` | Top-level string claim for Identity Provider usernames, used for display, username matching, and optional new-user naming. |
| New-user username            | No                         | Identity Provider username | Name newly provisioned Jellyfin Users from the Username claim (with verified-email fallback) or verified email only. Independent of automatic linking; existing usernames are unchanged. |
| Automatic linking            | No                         | Match email to Jellyfin username | Choose no automatic linking, verified email-to-username matching, or Identity Provider username-to-username matching. Existing links and explicit approvals take precedence. |
| Require verified email       | No                         | On                  | Require a supplied email with `email_verified=true` for linking and all Identity Provider sign-ins, including linked users. |
| Additional requested scopes  | No                         | -                   | Space-separated scopes requested in addition to `openid email profile`.                       |
| Login button text            | No                         | `Sign In with SSO`  | The text on the Jellyfin sign-in button.                                                      |
| Password login mode          | No                         | Allow for all users | Choose who can use local passwords.                                                           |
| Redirect sign-in page to provider | No                    | Off                 | Directly redirect to the Identity Provider when passwords are disabled for all users.          |
| RP-Initiated Logout          | No                         | Off                 | Also sign out from your OIDC service when it supports this.                                   |

The plugin supports one OIDC service and simple, top-level profile and group data. If your groups are nested, map them to a top-level claim in your OIDC service. You cannot set endpoint URLs by hand.

## Provider Setup

The plugin needs a confidential authorization-code client with the callback URL from the quick start. It requires top-level `sub` and a top-level group claim containing strings or a JSON string array. By default, `email` and `email_verified=true` are also required. Supply the configured top-level username claim, normally `preferred_username`, to name newly provisioned Jellyfin Users.

| Provider          | Setup                                                                                                                                                                                      |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Keycloak          | Add a Group Membership protocol mapper that emits a flat `groups` claim to the ID token or UserInfo endpoint. Disable full group paths unless those paths are the configured group values. |
| Authentik         | Ensure the configured provider returns `groups`, and map a verified source attribute to `email_verified`.                                                                                  |
| Authelia          | Set **Additional requested scopes** to `groups`. Configure the client to return the standard email claims and use `groups` as the group claim.                                             |
| ZITADEL           | Use an Action to project eligible project roles into a top-level flat string-array claim such as `groups`, then configure that claim here. The nested ZITADEL roles claim is unsupported.  |
| Pocket ID         | Configure a flat group claim and verified email claims, then test sign-in before disabling local passwords.                                                                                |

Do not add a scope solely because its name matches the group claim. Some providers expose groups without a scope, and some reject undeclared scopes.

## Sign In

> [!NOTE]
> A user needs a matching allowed or administrator group. **Require verified email** is enabled by default and applies to linking and every Identity Provider sign-in. When disabled, identities may sign in without email; new Jellyfin Users need a username claim or verified email, and automatic Email Match still requires a verified email.

1. The plugin first resolves the existing Identity Link by issuer + `sub`.
2. Without a link, it checks administrator approvals, then applies **Automatic linking**. Email matching requires current verified email; username matching uses the configured Username claim. Both compare case-insensitively. No automatic linking requires explicit approval or self-service linking to attach an existing Jellyfin User.
3. If it still finds no match, it creates a Jellyfin User using **New-user username**. The default uses the **Username claim**, falling back to verified email. **Verified email** requires current verified email, even if the general email requirement is off. Provisioning remains available in every matching mode. Existing username collisions and conflicting links or approvals are rejected.
4. It then updates administrator rights from the administrator groups.

Identity Provider sign-in preserves an existing Jellyfin username even when the verified email changes. It keeps the same Jellyfin User and viewing activity and does not merge users.

## Link Your Identity Provider

Sign in to Jellyfin, open your own **Profile**, and select **Link account** in the **SSO** card above the password fields. Authenticate with the configured Identity Provider, then return to the profile. Its status shows **Linked as your verified email**, or the Identity Provider username when no email is recorded. Older links without either value show **Linked** until a successful sign-in supplies display metadata. This works in both modern UI and legacy UI. The identity must belong to an allowed or administrator group and supply a verified email unless **Require verified email** is disabled.

Linking preserves your username, permissions, and current Jellyfin session. Under **Disable for linked users only**, it disables local-password sign-in immediately. Future Identity Provider sign-ins update administrator access from group membership and optionally synchronize your profile image.

Your profile hides the password fields and save/reset buttons when password sign-in is disabled for you. Under **Disable for linked users only**, unlinked Jellyfin Users keep those controls. Under **Disable for all users**, they are hidden for every Jellyfin User. Allowing local passwords again restores the controls, subject to Jellyfin's normal permissions.

Select **Unlink account** in your own profile to remove your Identity Link after confirmation. This signs out tracked OIDC sessions while preserving local-password, Quick Connect, and older untracked sessions. Automatic relinking is then disabled until a fresh profile link or administrator approval succeeds. If your current session originated through OIDC, it ends; make sure you can sign in again. The unlink confirmation uses the same Jellyfin component and styling as configuration-page confirmations.

Under **Disable for linked users only**, unlinking restores password sign-in without creating a password. Globally disabled passwords stay disabled. Link recovery and incomplete sign-out retries remain available while OIDC is unavailable. Linking cannot merge users or transfer an identity already linked to someone else.

The plugin retains supplied verified email and username metadata on the Identity Link and refreshes it on successful sign-in. Your authenticated profile and authorized administrator management can inspect those display values. Neither changes your existing Jellyfin username. Changing **Username claim** affects future provisioning, optional username matching, and refreshed display metadata, not existing Jellyfin usernames.

## Administrator User Management

Open **Dashboard > Users > select a Jellyfin User > OIDC**. The tab uses Jellyfin's existing components in modern and legacy UI.

To approve linking, select **Link on next sign-in**, choose **Match by**, enter one **Value**, and click **Approve link**:

- **Subject (sub)**: the Identity Provider's stable user identifier, matched exactly and case-sensitively. It is not a session ID.
- **Email**: a case-insensitive match requiring `email_verified=true` in the next sign-in, even when the global verified-email requirement is off.
- **Username**: a case-insensitive match of the configured Identity Provider Username claim. It need not equal the Jellyfin username. Edit the approval if that claim setting changes.

Approval does not authenticate on the selected user's behalf, immediately create an Identity Link, or change password availability. The next eligible matching sign-in uses the selected existing Jellyfin User and stores issuer + `sub` as its Identity Link. Requests persist without expiry until fulfilled, edited, or cancelled. They remain available when automatic linking is disabled. Existing links cannot be transferred, replaced, or merged by an approval.

**Unlink account** removes the Identity Link and signs out positively identified OIDC-origin Jellyfin sessions. Local-password and Quick Connect sessions remain active. Older sessions created before provenance tracking are unclassified and preserved, so historical OIDC sessions cannot all be selectively identified. Unlinking does not sign out at the Identity Provider or guarantee that buffered playback stops immediately.

Unlinking preserves the Jellyfin User and media activity and blocks automatic reattachment to that user. A fresh self-service link or fulfilled administrator approval clears the block. This is not an identity-wide access ban: ordinary provisioning may still create a different Jellyfin User when naming rules permit it.

Removing the final link restores local-password sign-in under **Disable for linked users only**, but does not create or reveal a password. **Disable for all users** remains in effect. Administrators can unlink themselves, including the sole administrator, after a warning about access loss and possible host-level recovery. Their current browser signs out only if its Jellyfin session originated through OIDC.

Link inspection, unlinking, and **Retry sign-out** remain available when OIDC is disabled, invalid, or reconfigured to a different issuer. A persistent partial-failure message identifies incomplete sign-out; retry only revokes tokens selected by that unlink, never newer sessions. Refresh after a version conflict before trying another change.

## Password Login

| Mode                          | Behavior                                                                         |
| ----------------------------- | -------------------------------------------------------------------------------- |
| Allow for all users           | Local passwords remain available.                                                |
| Disable for linked users only | Local passwords are unavailable for Jellyfin Users with an Identity Link. |
| Disable for all users         | Local passwords are unavailable for every Jellyfin User.                         |

When local passwords are disabled for all users, **Redirect sign-in page to provider** is available. Off keeps the Jellyfin sign-in URL and shows only the OIDC sign-in button. On redirects directly to the Identity Provider and provides an OIDC-only signed-out page.

> [!WARNING]
> When you change this setting, the plugin updates every Jellyfin user. Before you turn off local passwords for everyone, set an administrator group. Test OIDC sign-in in another browser session before you sign out.

### Emergency Recovery

If an OIDC outage or configuration mistake prevents every administrator from signing in, a Docker host administrator can restore local-password access. This requires an existing Jellyfin administrator password, it cannot recover the random passwords created for provisioned Jellyfin users.

1. Stop Jellyfin.
2. In the persistent config directory, change `<Enabled>true</Enabled>` to `<Enabled>false</Enabled>` in `plugins/configurations/Jellyfin.Plugin.Oidc.xml`.
3. If `/config` is bind-mounted from the Docker host, run this on the host. Replace the paths with yours:

   ```sh
   cp -a /host/path/to/jellyfin/config /host/path/to/jellyfin/config.backup
   sqlite3 /host/path/to/jellyfin/config/data/jellyfin.db <<'SQL'
   UPDATE Users
   SET AuthenticationProviderId = 'Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider'
   WHERE AuthenticationProviderId = 'Jellyfin.Plugin.Oidc.Identity.OidcPasswordDisabledProvider';
   SQL
   ```

   When `/config` is a named Docker volume, run the same SQL from a one-off SQLite container with that volume mounted instead.

4. Start Jellyfin, sign in with the known local administrator password, and correct the OIDC configuration before enabling it again.

This does not delete Jellyfin users or Identity Links.

## Custom Themes

This plugin provides custom CSS overrides to keep OIDC-only sign-in visually consistent with supported themes. Add the matching import directly after the theme import in Jellyfin's **Custom CSS**.

### Abyss

```css
@import url('https://cdn.jsdelivr.net/gh/edmogeor/jellyfin-plugin-oidc@main/assets/abyss-oidc.css');
```

### ElegantFin

```css
@import url('https://cdn.jsdelivr.net/gh/edmogeor/jellyfin-plugin-oidc@main/assets/elegantfin-oidc.css');
```

## Logout

Turn on **RP-Initiated Logout** to sign out from your OIDC service when the user signs out of Jellyfin. Add this post-logout redirect URL to your OIDC service:

```text
https://jellyfin.example.com/web/index.html
```

If the OIDC service cannot complete this sign-out, Jellyfin returns to its sign-in page.

## Development

```sh
make setup
make check
make test
```

| Command          | Description                                               |
| ---------------- | --------------------------------------------------------- |
| `make format`    | Apply CSharpier and Prettier.                             |
| `make lint`      | Run Roslyn analyzers and oxlint.                          |
| `make check`     | Verify formatting and linting.                            |
| `make test-unit` | Run the .NET unit suite.                                  |
| `make test-e2e`  | Reset the Docker stack and run headless Playwright tests. |
| `make up`        | Start the e2e stack for manual testing.                   |

The e2e stack runs Jellyfin 12, Keycloak, and Caddy at `https://localhost:8443`.
Run `ABYSS_THEME=1 make up` or `ELEGANTFIN_THEME=1 make up` to start it with the selected theme.

## Donations

Feel free to donate if you'd like to support development.

<a href="https://www.buymeacoffee.com/edmogeor" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" /></a>

## License

Copyright © 2026 edmogeor. Licensed under [GPL-3.0-or-later](LICENSE).
