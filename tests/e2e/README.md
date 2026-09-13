# OIDC Integration Stack

Runs Jellyfin 12, the built plugin, and a Keycloak realm with a verified-email test user and the required `groups` claim. It uses only `localhost`, so no host-file or browser-container setup is needed.

Install the host test browser once:

```sh
npm install
npx playwright install chromium
```

Run the headless e2e suite with the reset script:

```sh
./test.sh
```

`test.sh` removes persisted Docker state before rebuilding and configuring the stack. Do not run `npm test` directly against an existing stack because a prior test or manual change can leave a different password-login mode configured.

Start or refresh the test server for manual testing without deleting state:

```sh
./up.sh
```

Open `https://localhost:8443/web/index.html`. The generated development certificate is self-signed, so accept its warning once or import `tests/e2e/.tls/ca.crt` into your browser's trust store.

Use the Jellyfin dashboard to inspect or change the plugin settings at **Dashboard, Plugins, OIDC Authentication**. The initial local Jellyfin administrator is `root` with an empty password, and OIDC is configured with password login allowed. Keep the stack running after `./test.sh` to test manually in the same browser.

Select **Login with SSO** to see the Keycloak browser flow, then sign in with the seeded test user below.

| Setting        | Value                    |
| -------------- | ------------------------ |
| Test user      | `oidc-test`              |
| Password       | `oidc-test-password`     |
| Verified email | `oidc-test@example.test` |
| Allowed group  | `jellyfin-users`         |

Remove all test state with `docker compose down -v`.
