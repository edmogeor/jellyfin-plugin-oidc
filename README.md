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

OpenID Connect authentication for Jellyfin 12. Authenticate Jellyfin Users through one Identity Provider while Jellyfin remains the authority for permissions and media activity.

<!-- toc -->

- [Features](#features)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Sign-In And Provisioning](#sign-in-and-provisioning)
- [Password Login](#password-login)
- [Logout](#logout)
- [Development](#development)
- [License](#license)

<!-- tocstop -->

## Features

- OpenID Connect discovery, confidential authorization-code flow, PKCE, and HTTPS metadata
- Configurable OIDC sign-in button and optional automatic sign-in when local passwords are disabled
- Group-based access and Jellyfin administrator synchronization
- Jellyfin User provisioning and durable Identity Links based on the Identity Provider `sub` claim
- Verified-email matching for existing Jellyfin Users
- Optional RP-initiated logout
- Local-relative return URLs, undisclosed generated local passwords, and secret-free browser configuration

## Quick Start

1. Install the plugin in Jellyfin 12 and restart the server.
2. In the Identity Provider, register a confidential OIDC client with the callback URL:

   ```text
   https://jellyfin.example.com/oidc/callback
   ```

3. Open **Dashboard -> Plugins -> OIDC Authentication** and enter the issuer URL, client ID, client secret, and at least one Allowed Group or Administrator Group.
4. Enable OIDC and save the configuration.

When Jellyfin is behind a reverse proxy or available at more than one public address, set **Public Jellyfin URL override** to its public HTTPS URL. Otherwise, callback and logout URLs use the browser request origin and path base.

## Configuration

| Setting | Required | Default | Description |
| --- | --- | --- | --- |
| Enable OIDC | Yes | Disabled | Allows eligible Identity Provider users to sign in. |
| Public Jellyfin URL override | No | Browser request URL | Public HTTPS URL used for callback and logout URLs. |
| Issuer URL | Yes | - | Absolute HTTPS issuer URL used for OIDC discovery. |
| Client ID | Yes | - | Confidential OIDC client ID. |
| Client secret | Yes | - | Confidential OIDC client secret. |
| Allowed groups | One group setting required | - | Comma-separated groups permitted to sign in and be provisioned. |
| Administrator groups | One group setting required | - | Comma-separated groups permitted to sign in as Jellyfin administrators. |
| Group claim | No | `groups` | Top-level string or string-array claim containing group membership. |
| Login button text | No | `Login with SSO` | Text displayed on the Jellyfin sign-in page. |
| Password login mode | No | Allow for all users | Controls local-password availability. |
| RP-Initiated Logout | No | Disabled | Also signs out at the Identity Provider when supported. |

The plugin supports one Identity Provider. It accepts top-level `sub`, `email`, `email_verified`, and group claims only. Map nested group data to a top-level claim at the Identity Provider; nested claim paths and manual endpoint overrides are not supported.

## Sign-In And Provisioning

On sign-in, the plugin requires a non-empty `sub`, a non-empty verified email, and membership in an Allowed Group or Administrator Group.

1. An existing Identity Link is resolved by `sub`.
2. Otherwise, the verified email is matched case-insensitively with a Jellyfin User username.
3. If neither exists, the plugin provisions a Jellyfin User using the verified email as its username.
4. The plugin creates an Identity Link when needed and synchronizes administrator status from Administrator Group membership.

An Identity Link remains authoritative if the Identity Provider email changes. The plugin never merges Jellyfin Users or reassigns their media activity.

## Password Login

| Mode | Behavior |
| --- | --- |
| Allow for all users | Local passwords remain available. |
| Disable for linked users only | Local passwords are unavailable for Jellyfin Users with an Identity Link. |
| Disable for all users | Local passwords are unavailable for every Jellyfin User. |

Changing the mode reapplies the selected policy to every Jellyfin User. Configure an Administrator Group before disabling local passwords for all users, and verify OIDC sign-in in a separate browser session before signing out.

## Logout

Enable **RP-Initiated Logout** to redirect browser sessions with a protected ID token to the discovered Identity Provider end-session endpoint. Register this post-logout redirect URI with the Identity Provider:

```text
https://jellyfin.example.com/web/index.html
```

If no protected ID token or end-session endpoint is available, logout returns to the Jellyfin login page.

## Development

```sh
make setup
make check
make test
```

| Command | Description |
| --- | --- |
| `make format` | Apply CSharpier and Prettier. |
| `make lint` | Run Roslyn analyzers and oxlint. |
| `make check` | Verify formatting and linting. |
| `make test-unit` | Run the .NET unit suite. |
| `make test-e2e` | Reset the Docker stack and run headless Playwright tests. |
| `make up` | Start the e2e stack for manual testing. |

The e2e stack runs Jellyfin 12, Keycloak, and Caddy at `https://localhost:8443`.

## License

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html)
