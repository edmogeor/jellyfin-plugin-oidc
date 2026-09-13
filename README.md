<div align="center">
  <h1>OIDC Authentication for Jellyfin</h1>
  <p>
    <a href="https://github.com/edmogeor/jellyfin-plugin-oidc/actions/workflows/test.yml">
      <img src="https://github.com/edmogeor/jellyfin-plugin-oidc/actions/workflows/test.yml/badge.svg?branch=main" alt="CI" />
    </a>
    <a href="https://www.gnu.org/licenses/gpl-3.0.html">
      <img src="https://img.shields.io/badge/License-GPL--3.0--or--later-blue.svg" alt="License: GPL-3.0-or-later" />
    </a>
  </p>
</div>

Let people sign in to Jellyfin 12 with one OpenID Connect (OIDC) sign-in service. Jellyfin keeps control of user permissions and viewing activity.

<!-- toc -->

- [Features](#features)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [What Happens At Sign-In](#what-happens-at-sign-in)
- [Password Login](#password-login)
- [Logout](#logout)
- [Development](#development)
- [License](#license)

<!-- tocstop -->

## Features

- Sign in with any OIDC service that supports standard discovery
- Show a configurable sign-in button, or go straight to the sign-in service when local passwords are off
- Allow access and administrator rights based on groups
- Create Jellyfin users when eligible people sign in for the first time
- Match existing Jellyfin users by verified email address
- Sign out from both Jellyfin and the sign-in service when supported
- Keep secrets out of browser settings and logs

## Quick Start

1. Install the plugin in Jellyfin 12, then restart Jellyfin.
2. In your OIDC service, create a confidential client. Add this callback URL:

   ```text
   https://jellyfin.example.com/oidc/callback
   ```

3. Open **Dashboard > Plugins > OIDC Authentication**. Enter the issuer URL, client ID, client secret, and at least one allowed or administrator group.
4. Turn on OIDC and save your changes.

If Jellyfin is behind a reverse proxy or has more than one public address, set **Public Jellyfin URL override** to its public HTTPS address. Otherwise, the plugin uses the address in the browser.

## Configuration

| Setting | Required | Default | Description |
| --- | --- | --- | --- |
| Enable OIDC | Yes | Off | Lets people who meet your group rules sign in. |
| Public Jellyfin URL override | No | Browser address | The public HTTPS address to use for callbacks and sign-out. |
| Issuer URL | Yes | - | The HTTPS address of your OIDC service. |
| Client ID | Yes | - | The client ID from your OIDC service. |
| Client secret | Yes | - | The client secret from your OIDC service. |
| Allowed groups | At least one group setting | - | Comma-separated groups that can sign in. New Jellyfin users can be created for their members. |
| Administrator groups | At least one group setting | - | Comma-separated groups that can sign in as Jellyfin administrators. |
| Group claim | No | `groups` | The top-level claim that lists a person's groups. |
| Login button text | No | `Login with SSO` | The text on the Jellyfin sign-in button. |
| Password login mode | No | Allow for all users | Choose who can use local passwords. |
| RP-Initiated Logout | No | Off | Also sign out from your OIDC service when it supports this. |

The plugin supports one OIDC service. It uses only top-level `sub`, `email`, `email_verified`, and group claims. If your groups are nested, map them to a top-level claim in your OIDC service. You cannot set endpoint URLs by hand.

## What Happens At Sign-In

To sign in, a person must have a `sub` value, a verified email address, and a matching allowed or administrator group.

1. The plugin first looks for the person who has the same `sub` value.
2. If it finds no match, it looks for a Jellyfin username that matches the verified email address.
3. If it still finds no match, it creates a Jellyfin user with the verified email address as the username.
4. It then updates administrator rights from the administrator groups.

If a person's verified email address changes, the plugin keeps their existing Jellyfin user. It does not merge users or move viewing activity between them.

## Password Login

| Mode | Behavior |
| --- | --- |
| Allow for all users | Local passwords remain available. |
| Disable for linked users only | Local passwords are unavailable for Jellyfin users who have signed in with OIDC. |
| Disable for all users | Local passwords are unavailable for every Jellyfin User. |

When you change this setting, the plugin updates every Jellyfin user. Before you turn off local passwords for everyone, set an administrator group. Test OIDC sign-in in another browser session before you sign out.

## Logout

Turn on **RP-Initiated Logout** to sign out from your OIDC service when the person signs out of Jellyfin. Add this post-logout redirect URL to your OIDC service:

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

| Command | Description |
| --- | --- |
| `make format` | Format C# and test files. |
| `make lint` | Check C# and test code for problems. |
| `make check` | Check formatting and linting. |
| `make test-unit` | Run the .NET unit tests. |
| `make test-e2e` | Start a clean test environment and run browser tests. |
| `make up` | Start the test environment for manual testing. |

The e2e stack runs Jellyfin 12, Keycloak, and Caddy at `https://localhost:8443`.

## License

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html)
