import { expect, test as base } from "@playwright/test";

const pluginId = "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4";
const realm = "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin";
const password = "identity-test-password";

const test = base.extend({
  identity: async ({ request }, use) => {
    const login = await request.post("/Users/AuthenticateByName", {
      headers: {
        Authorization: `MediaBrowser Client="e2e", Device="e2e", DeviceId="identity-${Date.now()}", Version="1"`,
      },
      data: { Username: "root", Pw: "" },
    });
    expect(login.ok()).toBe(true);
    const session = await login.json();
    const headers = {
      Authorization: `MediaBrowser Token="${session.AccessToken}"`,
    };
    const config = await request
      .get(`/Plugins/${pluginId}/Configuration`, { headers })
      .then((response) => response.json());
    const token = await request
      .post(
        "https://oidc.localhost:8443/keycloak/realms/master/protocol/openid-connect/token",
        {
          form: {
            client_id: "admin-cli",
            grant_type: "password",
            username: "admin",
            password: "admin",
          },
        },
      )
      .then((response) => response.json());
    const providerHeaders = { Authorization: `Bearer ${token.access_token}` };
    const name = `identity-${Date.now()}-${Math.random().toString(16).slice(2, 7)}`;
    const provider = {
      username: name,
      email: `${name}@example.test`,
      emailVerified: true,
      firstName: "Identity",
      lastName: "Test",
      enabled: true,
      credentials: [{ type: "password", value: password, temporary: false }],
      groups: ["jellyfin-users"],
    };
    const created = await request.post(realm + "/users", {
      headers: providerHeaders,
      data: provider,
    });
    expect(created.ok()).toBe(true);
    const subject = created.headers().location.split("/").pop();
    const identity = {
      headers,
      config,
      providerHeaders,
      provider,
      subject,
      localUsers: [],
    };
    try {
      await use(identity);
    } finally {
      await request.post(`/Plugins/${pluginId}/Configuration`, {
        headers,
        data: config,
      });
      await request.delete(`${realm}/users/${subject}`, {
        headers: providerHeaders,
      });
      const users = await request
        .get("/Users", { headers })
        .then((response) => response.json());
      for (const user of users.filter(
        (user) =>
          identity.localUsers.includes(user.Id) ||
          user.Name === provider.email ||
          user.Name === provider.username,
      )) {
        await request.delete(`/Users/${user.Id}`, { headers });
      }
    }
  },
});

async function configuration(request, identity, values) {
  const config = await request
    .get(`/Plugins/${pluginId}/Configuration`, { headers: identity.headers })
    .then((response) => response.json());
  const response = await request.post(`/Plugins/${pluginId}/Configuration`, {
    headers: identity.headers,
    data: { ...config, ...values },
  });
  expect(response.ok()).toBe(true);
}

async function localUser(request, identity, name) {
  const response = await request.post("/Users/New", {
    headers: identity.headers,
    data: { Name: name, Password: password },
  });
  expect(response.ok()).toBe(true);
  const user = await response.json();
  identity.localUsers.push(user.Id);
  return user;
}

async function localSignIn(page, name, pw = password) {
  await page.goto("/web/index.html#!/login");
  await page.locator("#txtManualName").fill(name);
  await page.locator("#txtManualPassword").fill(pw);
  await page.locator("form.manualLoginForm button[type=submit]").click();
  await waitForUserMenu(page);
}

async function waitForUserMenu(page) {
  const menu = page
    .getByRole("button", { name: "User Menu" })
    .or(page.locator(".headerUserButton"))
    .filter({ visible: true });
  await expect(menu.first()).toBeVisible({ timeout: 30_000 });
}

async function linkSignIn(page, identity, label = "Link Identity Provider") {
  await Promise.all([
    page.waitForURL(/oidc\.localhost/, { waitUntil: "commit" }),
    page.getByRole("button", { name: label, exact: true }).click(),
  ]);
  await providerLogin(page, identity);
}

async function providerLogin(page, identity) {
  const username = page.locator("#username");
  const needsCredentials = await Promise.race([
    username.waitFor({ state: "visible", timeout: 30_000 }).then(() => true),
    page
      .waitForURL(/\/web\/index\.html/, { timeout: 30_000 })
      .then(() => false),
  ]);
  if (needsCredentials) {
    await username.fill(identity.provider.username);
    await page.locator("#password").fill(password);
    await page.locator("#kc-login").click();
  }
  await expect(page).toHaveURL(/\/web\/index\.html/, { timeout: 30_000 });
}

async function oidcSignIn(page, identity) {
  await page.goto("/oidc/start");
  await providerLogin(page, identity);
  await page.waitForFunction(
    () =>
      JSON.parse(localStorage.getItem("jellyfin_credentials") || "{}")
        .Servers?.[0]?.AccessToken,
  );
  await waitForUserMenu(page);
  return page.evaluate(async () => {
    const token = JSON.parse(localStorage.getItem("jellyfin_credentials"))
      .Servers[0].AccessToken;
    return fetch("/Users/Me", {
      headers: { Authorization: `MediaBrowser Token="${token}"` },
    }).then((response) => response.json());
  });
}

async function status(request, identity, userId) {
  const response = await request.get(`/oidc/identity/users/${userId}`, {
    headers: identity.headers,
  });
  expect(response.ok()).toBe(true);
  return response.json();
}

async function edit(page, userId) {
  await page.goto(`/web/index.html#/dashboard/users/${userId}/profile`);
  await page.getByRole("tab", { name: "OIDC", exact: true }).click();
  await expect(page.locator("#oidc-user-panel .oidc-card")).toBeVisible();
}

test("preferred username first sign-in matches once and preserves the local username", async ({
  page,
  request,
  identity,
}) => {
  const local = await localUser(
    request,
    identity,
    identity.provider.username.toUpperCase(),
  );
  await configuration(request, identity, {
    FirstSignInMatching: "PreferredUsername",
  });
  const user = await oidcSignIn(page, identity);
  expect(user.Id).toBe(local.Id);
  expect(user.Name).toBe(local.Name);
  const linked = await status(request, identity, user.Id);
  expect(linked.Origin).toBe("PreferredUsernameMatch");
  await request.put(`${realm}/users/${identity.subject}`, {
    headers: identity.providerHeaders,
    data: {
      ...identity.provider,
      email: "changed@example.test",
      emailVerified: false,
    },
  });
  await configuration(request, identity, {
    FirstSignInMatching: "VerifiedEmail",
  });
  const returning = await oidcSignIn(page, identity);
  expect(returning.Id).toBe(local.Id);
  expect(returning.Name).toBe(local.Name);
  expect((await status(request, identity, local.Id)).Email).toBe("");
});

test("Open enrollment admits missing groups, provisions non-admin users and demotes returning administrators", async ({
  page,
  request,
  identity,
}) => {
  const group = await request
    .get(realm + "/groups", { headers: identity.providerHeaders })
    .then((response) => response.json());
  const usersGroup = group.find((item) => item.name === "jellyfin-users");
  await request.delete(
    `${realm}/users/${identity.subject}/groups/${usersGroup.id}`,
    { headers: identity.providerHeaders },
  );
  await configuration(request, identity, {
    UserGroup: "",
    AdministratorGroup: "",
    GroupClaim: "missing_group_claim",
  });
  const user = await oidcSignIn(page, identity);
  expect(user.Name).toBe(identity.provider.email);
  expect(user.Policy.IsAdministrator).toBe(false);
  await request.post(`/Users/${user.Id}/Policy`, {
    headers: identity.headers,
    data: { ...user.Policy, IsAdministrator: true },
  });
  const returning = await oidcSignIn(page, identity);
  expect(returning.Policy.IsAdministrator).toBe(false);
  await configuration(request, identity, { UserGroup: "jellyfin-users" });
  await page.goto("/oidc/start");
  await providerLogin(page, identity);
  await expect(page).toHaveURL(/oidcError=1/);
});

test("self-service links preserve the session, unlink opts out, and explicit relinking restores access", async ({
  page,
  request,
  identity,
}) => {
  const local = await localUser(
    request,
    identity,
    "local-" + identity.provider.username,
  );
  await localSignIn(page, local.Name);
  await configuration(request, identity, {
    PasswordLoginMode: "DisableForLinkedUsersOnly",
  });
  const original = await page.evaluate(
    () =>
      JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
        .AccessToken,
  );
  await page.goto("/web/index.html#/userprofile");
  await linkSignIn(page, identity);
  await expect(page.getByText("Linked to SSO", { exact: true })).toBeVisible();
  expect(
    await page.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
          .AccessToken,
    ),
  ).toBe(original);
  const summary = await status(request, identity, local.Id);
  expect(summary.Email).toBe(identity.provider.email);
  expect(summary.PreferredUsername).toBe(identity.provider.username);
  expect(summary.Origin).toBe("Explicit");
  const linkedPolicy = await request
    .get(`/Users/${local.Id}`, { headers: identity.headers })
    .then((response) => response.json());
  expect(linkedPolicy.Policy.AuthenticationProviderId).toContain(
    "OidcPasswordDisabledProvider",
  );
  await page.getByRole("button", { name: "Unlink", exact: true }).click();
  await page.getByRole("button", { name: "Confirm", exact: true }).click();
  await expect(page.getByText("Not linked", { exact: true })).toBeVisible();
  const unlinkedPolicy = await request
    .get(`/Users/${local.Id}`, { headers: identity.headers })
    .then((response) => response.json());
  expect(unlinkedPolicy.Policy.AuthenticationProviderId).toContain(
    "DefaultAuthenticationProvider",
  );
  await page.goto("/oidc/start");
  await providerLogin(page, identity);
  await expect(page).toHaveURL(/oidcError=1/);
  await page.goto("/web/index.html#/userprofile");
  await linkSignIn(page, identity);
  await expect(page.getByText("Linked to SSO", { exact: true })).toBeVisible();
  expect((await status(request, identity, local.Id)).HasOptOut).toBe(false);
  await configuration(request, identity, {
    AllowSelfServiceIdentityLinks: false,
  });
  await page.evaluate(() =>
    dispatchEvent(new Event("oidcconfigurationchange")),
  );
  await expect(
    page.getByRole("button", { name: "Unlink", exact: true }),
  ).toHaveCount(0);
  await expect(page.getByText("Linked to SSO", { exact: true })).toBeVisible();
});

test("admin link-by-sign-in requires confirmation and retains the administrator session", async ({
  page,
  request,
  identity,
}) => {
  const local = await localUser(
    request,
    identity,
    "admin-target-" + identity.provider.username,
  );
  await localSignIn(page, "root", "");
  const original = await page.evaluate(
    () =>
      JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
        .AccessToken,
  );
  await edit(page, local.Id);
  await linkSignIn(page, identity, "Link by signing in");
  await expect(page.getByRole("dialog")).toContainText(local.Name);
  await expect(page.getByRole("dialog")).toContainText(identity.provider.email);
  expect((await status(request, identity, local.Id)).Linked).toBe(false);
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  expect((await status(request, identity, local.Id)).Linked).toBe(false);
  await linkSignIn(page, identity, "Link by signing in");
  await page.getByRole("button", { name: "Confirm", exact: true }).click();
  await expect(page.getByText("Linked to SSO", { exact: true })).toBeVisible();
  expect(
    await page.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
          .AccessToken,
    ),
  ).toBe(original);
  expect((await status(request, identity, local.Id)).Linked).toBe(true);
  await expect(
    page.getByRole("textbox", { name: "Expected email or preferred username" }),
  ).toHaveCount(0);
});

test("Pending Admin Matches consume their claim once and survive a stale configuration save", async ({
  page,
  request,
  identity,
}) => {
  const local = await localUser(
    request,
    identity,
    "pending-" + identity.provider.username,
  );
  const stale = await request
    .get(`/Plugins/${pluginId}/Configuration`, { headers: identity.headers })
    .then((response) => response.json());
  const before = await status(request, identity, local.Id);
  const response = await request.post(
    `/oidc/identity/users/${local.Id}/pending`,
    {
      headers: identity.headers,
      data: {
        Claim: "PreferredUsername",
        Value: identity.provider.username,
        Revision: before.Revision,
      },
    },
  );
  expect(response.status()).toBe(204);
  expect(
    (
      await request.post(`/oidc/identity/users/${local.Id}/unlink`, {
        headers: identity.headers,
        data: { Revision: before.Revision },
      })
    ).status(),
  ).toBe(409);
  await request.post(`/Plugins/${pluginId}/Configuration`, {
    headers: identity.headers,
    data: stale,
  });
  expect((await status(request, identity, local.Id)).PendingMatch.Value).toBe(
    identity.provider.username,
  );
  const user = await oidcSignIn(page, identity);
  expect(user.Id).toBe(local.Id);
  const linked = await status(request, identity, local.Id);
  expect(linked.PendingMatch).toBeFalsy();
  expect(linked.Origin).toBe("Explicit");
  expect(
    (
      await request.post(`/oidc/identity/users/${local.Id}/unlink`, {
        headers: identity.headers,
        data: { Revision: linked.Revision },
      })
    ).status(),
  ).toBe(204);
  const unlinked = await status(request, identity, local.Id);
  expect(unlinked.HasOptOut).toBe(true);
  expect(
    (
      await request.post(`/oidc/identity/users/${local.Id}/pending`, {
        headers: identity.headers,
        data: {
          Claim: "VerifiedEmail",
          Value: identity.provider.email,
          Revision: unlinked.Revision,
        },
      })
    ).status(),
  ).toBe(204);
  expect((await oidcSignIn(page, identity)).Id).toBe(local.Id);
  expect((await status(request, identity, local.Id)).HasOptOut).toBe(false);
});

test("concurrent administrator matches reject stale and duplicate reservations without overwriting", async ({
  request,
  identity,
}) => {
  const first = await localUser(
    request,
    identity,
    "race-first-" + identity.provider.username,
  );
  const second = await localUser(
    request,
    identity,
    "race-second-" + identity.provider.username,
  );
  const before = await status(request, identity, first.Id);
  const data = {
    Claim: "VerifiedEmail",
    Value: identity.provider.email,
    Revision: before.Revision,
  };
  const responses = await Promise.all(
    [first, second].map((user) =>
      request.post(`/oidc/identity/users/${user.Id}/pending`, {
        headers: identity.headers,
        data,
      }),
    ),
  );
  expect(responses.map((response) => response.status()).sort()).toEqual([
    204, 409,
  ]);
  const winner = responses[0].status() === 204 ? first : second;
  const loser = responses[0].status() === 204 ? second : first;
  const current = await status(request, identity, loser.Id);
  expect(
    (
      await request.post(`/oidc/identity/users/${loser.Id}/pending`, {
        headers: identity.headers,
        data: { ...data, Revision: current.Revision },
      })
    ).status(),
  ).toBe(409);
  expect(
    (
      await request.post(`/oidc/identity/users/${winner.Id}/pending`, {
        headers: identity.headers,
        data: { ...data, Value: "", Revision: current.Revision },
      })
    ).status(),
  ).toBe(204);
  expect((await status(request, identity, winner.Id)).PendingMatch).toBeFalsy();
});

test("profile APIs reject other users, cross-session tickets, and disabled in-flight linking", async ({
  page,
  request,
  identity,
}) => {
  const local = await localUser(
    request,
    identity,
    "security-" + identity.provider.username,
  );
  await localSignIn(page, local.Name);
  const token = await page.evaluate(
    () =>
      JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
        .AccessToken,
  );
  const headers = { Authorization: `MediaBrowser Token="${token}"` };
  expect(
    (
      await request.get(`/oidc/identity/users/${local.Id}`, { headers })
    ).status(),
  ).toBe(403);
  expect((await request.get("/oidc/identity/me")).status()).toBe(401);
  const before = await request
    .get("/oidc/identity/me", { headers })
    .then((response) => response.json());
  expect(before).not.toHaveProperty("Subject");
  expect(before).not.toHaveProperty("Issuer");
  const start = await request
    .post("/oidc/identity/me/start", {
      headers,
      data: { ReturnUrl: "/userprofile", Revision: before.Revision },
    })
    .then((response) => response.json());
  await page.route("**/oidc/identity/review", (route) => route.abort());
  await page.goto(start.Url);
  await providerLogin(page, identity);
  const ticket = await page.evaluate(
    () =>
      performance
        .getEntriesByType("navigation")[0]
        .name.match(/oidcLinkTicket=([^&#]+)/)?.[1],
  );
  expect(ticket).toBeTruthy();
  expect(
    (
      await request.post("/oidc/identity/complete", {
        headers: identity.headers,
        data: { Ticket: ticket },
      })
    ).status(),
  ).toBe(400);
  await configuration(request, identity, {
    AllowSelfServiceIdentityLinks: false,
  });
  expect(
    (
      await request.post("/oidc/identity/complete", {
        headers,
        data: { Ticket: ticket },
      })
    ).status(),
  ).toBe(400);
  expect(
    (
      await request.post("/oidc/identity/me/unlink", {
        headers,
        data: { Revision: before.Revision },
      })
    ).status(),
  ).toBe(409);
  expect((await status(request, identity, local.Id)).Linked).toBe(false);
});

test("OIDC tab resets across edited users, New User saves a pending identity and reports partial failures", async ({
  page,
  request,
  identity,
}) => {
  await localSignIn(page, "root", "");
  const first = await localUser(
    request,
    identity,
    "first-" + identity.provider.username,
  );
  const second = await localUser(
    request,
    identity,
    "second-" + identity.provider.username,
  );
  for (const local of [first, second, first]) {
    await edit(page, local.Id);
    await expect(page.locator("#oidc-user-panel")).toContainText(local.Name);
    await expect(
      page.getByRole("tab", { name: "OIDC", exact: true }),
    ).toHaveCount(1);
  }
  await page.goto("/web/index.html#/dashboard/users");
  await page.getByRole("button", { name: "Add User", exact: true }).click();
  await page.locator("#txtUsername").fill("new-" + identity.provider.username);
  await page.locator("#txtPassword").fill(password);
  await page.locator("#oidc-new-claim").selectOption("PreferredUsername");
  await page.locator("#oidc-new-value").fill(identity.provider.username);
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.locator("#oidc-user-panel")).toBeVisible();
  await expect(page.locator("#oidc-edit-value")).toHaveValue(
    identity.provider.username,
  );
  const createdId = await page.evaluate(
    () => location.hash.match(/users\/([^/]+)/)[1],
  );
  identity.localUsers.push(createdId);
  expect((await status(request, identity, createdId)).PendingMatch.Value).toBe(
    identity.provider.username,
  );
  await page.goto("/web/index.html#/dashboard/users");
  await page.getByRole("button", { name: "Add User", exact: true }).click();
  await expect(page.locator("#oidc-new-value")).toHaveValue("");
  await page
    .locator("#txtUsername")
    .fill("partial-" + identity.provider.username);
  await page.locator("#oidc-new-value").fill(identity.provider.email);
  await page.route("**/oidc/identity/users/*/pending", (route) =>
    route.fulfill({ status: 503, contentType: "application/json", body: "{}" }),
  );
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.locator("#oidc-user-panel [role=alert]")).toContainText(
    "was created",
  );
  identity.localUsers.push(
    await page.evaluate(() => location.hash.match(/users\/([^/]+)/)[1]),
  );
});

test("all-password mode hides both password surfaces, restores them, and forbids self-service unlink", async ({
  browser,
  page,
  request,
  identity,
}) => {
  await localSignIn(page, "root", "");
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const userPage = await context.newPage();
  const user = await oidcSignIn(userPage, identity);
  const token = await userPage.evaluate(
    () =>
      JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
        .AccessToken,
  );
  await configuration(request, identity, {
    PasswordLoginMode: "DisableForAllUsers",
  });
  await userPage.goto("/web/index.html#/userprofile");
  await expect(userPage.locator(".passwordSection")).toBeHidden();
  await expect(
    userPage.getByRole("button", { name: "Unlink", exact: true }),
  ).toBeDisabled();
  const linked = await status(request, identity, user.Id);
  expect(
    (
      await request.post("/oidc/identity/me/unlink", {
        headers: { Authorization: `MediaBrowser Token="${token}"` },
        data: { Revision: linked.Revision },
      })
    ).status(),
  ).toBe(409);
  await edit(page, user.Id);
  await expect(
    page.getByRole("tab", { name: "Password", exact: true }),
  ).toHaveCount(0);
  await page.goto(`/web/index.html#/dashboard/users/${user.Id}/password`);
  await expect(
    page.locator('#usersEditPage input[type="password"]').first(),
  ).toBeHidden();
  await configuration(request, identity, {
    PasswordLoginMode: "AllowForAllUsers",
  });
  await edit(page, user.Id);
  await expect(
    page.getByRole("tab", { name: "Password", exact: true }),
  ).toBeVisible();
  await userPage.evaluate(() =>
    dispatchEvent(new Event("oidcconfigurationchange")),
  );
  await expect(userPage.locator(".passwordSection")).toBeVisible();
  await context.close();
});

test("provider branding uses local logos, generated wording, custom overrides, and fallback icons", async ({
  page,
  request,
  identity,
}) => {
  await configuration(request, identity, {
    ProviderBrand: "google",
    LoginButtonText: "",
  });
  await page.goto("/web/index.html#!/login");
  const login = page.getByRole("button", {
    name: "Sign in with Google",
    exact: true,
  });
  await expect(login).toBeVisible();
  await expect(login.locator("img")).toHaveAttribute(
    "src",
    /\/oidc\/icons\/google$/,
  );
  await configuration(request, identity, {
    LoginButtonText: "Custom identity sign-in",
  });
  await page.evaluate(() =>
    dispatchEvent(new Event("oidcconfigurationchange")),
  );
  await expect(
    page.getByRole("button", { name: "Custom identity sign-in" }),
  ).toBeVisible();
  await configuration(request, identity, {
    ProviderBrand: "Other",
    LoginButtonText: "",
  });
  await page.evaluate(() =>
    dispatchEvent(new Event("oidcconfigurationchange")),
  );
  await expect(
    page
      .getByRole("button", { name: "Sign in with SSO", exact: true })
      .locator(".material-icons"),
  ).toHaveText("vpn_key");
  const publicConfig = await request
    .get("/oidc/config")
    .then((response) => response.json());
  expect(publicConfig).not.toHaveProperty("IdentityLinks");
  expect(publicConfig).not.toHaveProperty("ClientSecret");
  for (const brand of [
    "auth0",
    "authentik",
    "fusionauth",
    "google",
    "keycloak",
    "microsoft-entra-id",
    "okta",
    "zitadel",
  ]) {
    const response = await request.get("/oidc/icons/" + brand);
    expect(response.ok()).toBe(true);
    expect(response.headers()["content-type"]).toContain("image/svg+xml");
  }
});

for (const layout of ["desktop-legacy", "mobile-legacy"]) {
  test(`${layout}: branded sign-in and native logout show the same OIDC-only button`, async ({
    page,
    request,
    identity,
  }) => {
    await page.addInitScript(
      (value) => localStorage.setItem("layout", value),
      layout,
    );
    if (layout === "mobile-legacy")
      await page.setViewportSize({ width: 390, height: 844 });
    await configuration(request, identity, {
      ProviderBrand: "keycloak",
      LoginButtonText: "",
    });
    await page.goto("/web/index.html#!/login");
    const login = page.getByRole("button", {
      name: "Sign in with Keycloak",
      exact: true,
    });
    await expect(login.locator("img")).toHaveAttribute(
      "src",
      /\/oidc\/icons\/keycloak$/,
    );
    await login.click();
    await page.waitForURL(/oidc\.localhost/);
    await providerLogin(page, identity);
    await waitForUserMenu(page);
    await configuration(request, identity, {
      PasswordLoginMode: "DisableForAllUsers",
      RedirectSignInPageToProvider: true,
      RpInitiatedLogout: false,
    });
    await page.evaluate(() =>
      dispatchEvent(new Event("oidcconfigurationchange")),
    );
    await page.waitForFunction(
      () => window.oidcRedirectSignInPageToProvider === true,
    );
    await page.locator(".headerUserButton:visible").click();
    await page.locator(".btnLogout.listItem-border").click();
    await expect(page).toHaveURL(/oidcSignedOut=1/);
    await expect(login).toBeVisible();
    await expect(login.locator("img")).toHaveAttribute(
      "src",
      /\/oidc\/icons\/keycloak$/,
    );
    await expect(
      page.getByRole("heading", { name: "Signed Out", exact: true }),
    ).toBeVisible();
  });

  test(`${layout}: profile linking survives cached-page visits and restores password controls`, async ({
    page,
    request,
    identity,
  }) => {
    await page.addInitScript(
      (value) => localStorage.setItem("layout", value),
      layout,
    );
    if (layout === "mobile-legacy")
      await page.setViewportSize({ width: 390, height: 844 });
    const local = await localUser(
      request,
      identity,
      "legacy-" + identity.provider.username,
    );
    await localSignIn(page, local.Name);
    const token = await page.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
          .AccessToken,
    );
    for (let visit = 0; visit < 2; visit++) {
      await page.goto(`/web/index.html#/userprofile?userId=${local.Id}`);
      await expect(page.locator("[data-oidc-surface]:visible")).toHaveCount(1);
      await expect(
        page.getByRole("button", { name: "Link Identity Provider" }),
      ).toBeVisible();
      await page.goto("/web/index.html#/home");
    }
    await page.goto(`/web/index.html#/userprofile?userId=${local.Id}`);
    await linkSignIn(page, identity);
    await expect(
      page.getByText("Linked to SSO", { exact: true }),
    ).toBeVisible();
    expect(
      await page.evaluate(
        () =>
          JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0]
            .AccessToken,
      ),
    ).toBe(token);
    await configuration(request, identity, {
      PasswordLoginMode: "DisableForAllUsers",
    });
    await page.goto("/web/index.html#/home");
    await page.goto(`/web/index.html#/userprofile?userId=${local.Id}`);
    await expect(
      page.locator("#userProfilePage .passwordSection"),
    ).toBeHidden();
    await expect(
      page.getByRole("button", { name: "Unlink", exact: true }),
    ).toBeDisabled();
    await configuration(request, identity, {
      PasswordLoginMode: "AllowForAllUsers",
    });
    await page.goto("/web/index.html#/home");
    await page.goto(`/web/index.html#/userprofile?userId=${local.Id}`);
    await expect(
      page.locator("#userProfilePage .passwordSection"),
    ).toBeVisible();
    await page.getByRole("button", { name: "Unlink", exact: true }).click();
    await page.getByRole("button", { name: "Confirm", exact: true }).click();
    await expect(page.getByText("Not linked", { exact: true })).toBeVisible();
    expect((await status(request, identity, local.Id)).HasOptOut).toBe(true);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBeLessThanOrEqual(await page.evaluate(() => innerWidth));
  });

  test(`${layout}: administrator linking and New User matching retain native navigation`, async ({
    page,
    request,
    identity,
  }) => {
    await page.addInitScript(
      (value) => localStorage.setItem("layout", value),
      layout,
    );
    if (layout === "mobile-legacy")
      await page.setViewportSize({ width: 390, height: 844 });
    await localSignIn(page, "root", "");
    const local = await localUser(
      request,
      identity,
      "legacy-admin-" + identity.provider.username,
    );
    await edit(page, local.Id);
    await page.getByRole("tab", { name: "Profile", exact: true }).click();
    await expect(page.locator(".editUserProfileForm")).toBeVisible();
    await page.getByRole("tab", { name: "OIDC", exact: true }).click();
    await linkSignIn(page, identity, "Link by signing in");
    await expect(page.getByRole("dialog")).toContainText(local.Name);
    await page.getByRole("button", { name: "Confirm", exact: true }).click();
    await expect(page.locator("#oidc-user-panel")).toContainText(
      "Linked to SSO",
    );
    await expect(
      page.getByRole("tab", { name: "OIDC", exact: true }),
    ).toHaveCount(1);
    await page.goto("/web/index.html#/dashboard/users");
    await page.getByRole("button", { name: "Add User", exact: true }).click();
    await page
      .locator("#txtUsername")
      .fill("legacy-new-" + identity.provider.username);
    await page
      .locator("#oidc-new-value")
      .fill("pending-" + identity.provider.email);
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.locator("#oidc-user-panel")).toBeVisible();
    await expect(page.locator("#oidc-edit-value")).toHaveValue(
      "pending-" + identity.provider.email,
    );
    const createdId = await page.evaluate(
      () => location.hash.match(/users\/([^/]+)/)[1],
    );
    identity.localUsers.push(createdId);
    expect(
      (await status(request, identity, createdId)).PendingMatch.Value,
    ).toBe("pending-" + identity.provider.email);
  });
}
