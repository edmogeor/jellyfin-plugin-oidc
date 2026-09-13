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
    <a href="https://www.gnu.org/licenses/gpl-3.0.html">
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
- [Password Login](#password-login)
- [Logout](#logout)
- [Development](#development)
- [Donations](#donations)
- [License](#license)

<!-- tocstop -->

## Features

- Sign in with an OIDC service that supports standard discovery and can return verified email and flat group claims
- Show a configurable sign-in button, or go straight to the sign-in service when local passwords are off
- Allow access and administrator rights based on groups
- Create Jellyfin users when eligible users sign in for the first time
- Match existing Jellyfin users by verified email address
- Sign out from both Jellyfin and the sign-in service when supported
- Localized OIDC configuration page: Danish, German, English (US), Spanish, Finnish, French, Italian, Japanese, Korean, Norwegian Bokmal, Dutch, Polish, Portuguese (Brazil), Russian, Swedish, and Chinese (Simplified)

## Prerequisites

- Jellyfin 12 or later
- An OIDC service with a configured client ID, client secret, and callback URL. See [Provider Setup](#provider-setup).
- At least one allowed or administrator group configured in the plugin.
- Existing Jellyfin users must have usernames that case-insensitively match their verified OIDC email before their first OIDC sign-in.
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
| Additional requested scopes  | No                         | -                   | Space-separated scopes requested in addition to `openid email profile`.                       |
| Login button text            | No                         | `Login with SSO`    | The text on the Jellyfin sign-in button.                                                      |
| Password login mode          | No                         | Allow for all users | Choose who can use local passwords.                                                           |
| RP-Initiated Logout          | No                         | Off                 | Also sign out from your OIDC service when it supports this.                                   |

The plugin supports one OIDC service and simple, top-level profile and group data. If your groups are nested, map them to a top-level claim in your OIDC service. You cannot set endpoint URLs by hand.

## Provider Setup

The plugin needs a confidential authorization-code client with the callback URL from the quick start. It requires top-level `sub`, `email`, and `email_verified` claims and a top-level group claim containing strings or a JSON string array.

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
> To sign in, a user needs a verified email address and a matching allowed or administrator group.

1. The plugin first looks for the user's existing Jellyfin user.
2. If it finds no match, it looks for a Jellyfin username that matches the verified email address.
3. If it still finds no match, it creates a Jellyfin user with the verified email address as the username.
4. It then updates administrator rights from the administrator groups.

If a user's verified email address changes, the plugin updates the linked Jellyfin username. It keeps the same Jellyfin user and its viewing activity. It does not merge users.

## Password Login

| Mode                          | Behavior                                                                         |
| ----------------------------- | -------------------------------------------------------------------------------- |
| Allow for all users           | Local passwords remain available.                                                |
| Disable for linked users only | Local passwords are unavailable for Jellyfin users who have signed in with OIDC. |
| Disable for all users         | Local passwords are unavailable for every Jellyfin User.                         |

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

## Donations

Feel free to donate if you'd like to support development.

<a href="https://www.buymeacoffee.com/edmogeor" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" /></a>

## License

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html)
