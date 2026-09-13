import { expect, test } from "@playwright/test";

let loginAttempt = 0;

async function signIn(page) {
  await page.goto("/web/index.html");
  await page
    .getByRole("button", { name: "Login with SSO" })
    .click({ noWaitAfter: true });
  await page.locator("#username").fill("oidc-test");
  await page.locator("#password").fill("oidc-test-password");
  await page.locator("#kc-login").click({ noWaitAfter: true });
  await expect(page).toHaveURL(/\/web\/index\.html/, { timeout: 30_000 });
  await page.waitForFunction(async () => {
    const server = JSON.parse(
      localStorage.getItem("jellyfin_credentials") || "{}",
    ).Servers?.[0];
    if (!server?.AccessToken) return false;
    return fetch("/Users/Me", {
      headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` },
    })
      .then((response) => response.ok)
      .catch(() => false);
  });
  await expect(page.getByRole("button", { name: "User Menu" })).toBeVisible({
    timeout: 30_000,
  });
}

async function signInDenied(page) {
  await page.goto("/web/index.html");
  await page
    .getByRole("button", { name: "Login with SSO" })
    .click({ noWaitAfter: true });
  await page.locator("#username").fill("oidc-test");
  await page.locator("#password").fill("oidc-test-password");
  await page.locator("#kc-login").click({ noWaitAfter: true });
  await expect(page).toHaveURL(/oidcError=1/, { timeout: 30_000 });
}

async function setPasswordLoginMode(page, mode) {
  await page.evaluate(async (passwordLoginMode) => {
    const config = await ApiClient.getPluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
    );
    config.PasswordLoginMode = passwordLoginMode;
    await ApiClient.updatePluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      config,
    );
  }, mode);
}

async function showLogin(page) {
  loginAttempt += 1;
  await page.goto(`/web/index.html?oidcTest=${loginAttempt}#!/login`);
}

async function providerAdmin(request) {
  const response = await request.post(
    "https://oidc.localhost:8443/keycloak/realms/master/protocol/openid-connect/token",
    {
      form: {
        client_id: "admin-cli",
        grant_type: "password",
        username: "admin",
        password: "admin",
      },
    },
  );
  expect(response.ok()).toBe(true);
  return { Authorization: `Bearer ${(await response.json()).access_token}` };
}

async function providerUser(request, headers) {
  const response = await request.get(
    "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users",
    {
      headers,
      params: { username: "oidc-test", exact: "true" },
    },
  );
  expect(response.ok()).toBe(true);
  const [user] = await response.json();
  expect(user).toBeTruthy();
  return user;
}

async function providerGroup(request, headers, name) {
  const response = await request.get(
    "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/groups",
    {
      headers,
      params: { search: name, exact: "true" },
    },
  );
  expect(response.ok()).toBe(true);
  const [group] = await response.json();
  expect(group).toBeTruthy();
  return group;
}

async function authenticatePassword(request, username, password) {
  return request.post("https://localhost:8443/Users/AuthenticateByName", {
    headers: {
      Authorization:
        'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e", Version="1"',
    },
    data: { Username: username, Pw: password },
  });
}

async function currentUser(page) {
  return page.evaluate(async () => {
    const server = JSON.parse(localStorage.getItem("jellyfin_credentials"))
      .Servers[0];
    return fetch("/Users/Me", {
      headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` },
    }).then((response) => response.json());
  });
}

test("provisions the allowed OIDC test user and starts a Jellyfin session", async ({
  page,
}) => {
  await signIn(page);

  const user = await currentUser(page);

  expect(user.Name).toBe("oidc-test@example.test");
  expect(user.Policy.IsAdministrator).toBe(true);
});

test("synchronizes a profile image from the OIDC picture claim", async ({
  browser,
  page,
}) => {
  await signIn(page);
  expect((await currentUser(page)).PrimaryImageTag).toBeFalsy();

  const original = await page.evaluate(async () => {
    const config = await ApiClient.getPluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
    );
    const originalValue = config.SynchronizeProfileImages;
    config.SynchronizeProfileImages = true;
    await ApiClient.updatePluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      config,
    );
    return originalValue;
  });

  try {
    const profileImageContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const profileImagePage = await profileImageContext.newPage();
    await signIn(profileImagePage);
    expect((await currentUser(profileImagePage)).PrimaryImageTag).toBeTruthy();
    await profileImageContext.close();
  } finally {
    await page.evaluate(async (originalValue) => {
      const config = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      config.SynchronizeProfileImages = originalValue;
      await ApiClient.updatePluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
        config,
      );
    }, original);
  }
});

test("shows an OIDC sign-in error on the login page", async ({ page }) => {
  await page.goto("/web/index.html#!/login?oidcError=1");
  await expect(page.locator(".toast")).toHaveText(
    "We couldn't sign you in. Please try again.",
  );
});

test("loads the seeded OIDC settings and validates changes for an administrator", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(page.locator("#Enabled")).toBeChecked({ timeout: 30_000 });
  await expect(page.locator("#PublicUrl")).toHaveValue(
    "https://localhost:8443",
  );
  await expect(page.locator("#IssuerUrl")).toHaveValue(
    "https://oidc.localhost:8443/keycloak/realms/jellyfin",
  );
  await expect(page.locator("#ClientId")).toHaveValue("jellyfin");
  await expect(page.locator("#UserGroup")).toHaveValue("jellyfin-users");
  await expect(page.locator("#AdministratorGroup")).toHaveValue(
    "jellyfin-admins",
  );
  await expect(page.locator("#SynchronizeProfileImages")).not.toBeChecked();
  await expect(page.locator("#SaveButton")).toBeDisabled();

  await page.getByText("Synchronize profile images", { exact: true }).click();
  await expect(page.locator("#SaveButton")).toBeEnabled();
  await page.getByText("Synchronize profile images", { exact: true }).click();
  await expect(page.locator("#SaveButton")).toBeDisabled();

  await page.locator("#IssuerUrl").fill("");
  await expect(page.locator("#SaveButton")).toBeEnabled();
  await page.locator("#SaveButton").click();
  expect(
    await page
      .locator("#IssuerUrl")
      .evaluate((input) => input.validity.valueMissing),
  ).toBe(true);

  await page
    .locator("#IssuerUrl")
    .fill("https://oidc.localhost:8443/keycloak/realms/jellyfin");
  await expect(page.locator("#SaveButton")).toBeDisabled();

  await page
    .locator("#PasswordLoginMode")
    .selectOption("DisableForLinkedUsersOnly");
  await page.locator("#SaveButton").click();
  await expect(
    page.getByRole("heading", { name: "Confirm password login change" }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "test OIDC sign-in in a separate browser session before signing out.",
    ),
  ).toBeVisible();
  await page.getByRole("button", { name: "Cancel" }).click();
});

test("only skips the Jellyfin login form when local passwords are disabled for all users", async ({
  browser,
  page,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  await adminPage.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(adminPage.locator("#Enabled")).toBeChecked({ timeout: 30_000 });

  await setPasswordLoginMode(adminPage, "AllowForAllUsers");
  await showLogin(page);
  await expect(
    page.getByRole("button", { name: "Login with SSO" }),
  ).toBeVisible();
  await expect(page.locator(".btnQuick")).toBeVisible();

  await setPasswordLoginMode(adminPage, "DisableForLinkedUsersOnly");
  await showLogin(page);
  await expect(
    page.getByRole("button", { name: "Login with SSO" }),
  ).toBeVisible();
  await expect(page.locator(".btnQuick")).toBeVisible();

  await setPasswordLoginMode(adminPage, "DisableForAllUsers");
  await showLogin(page);
  await expect(page).toHaveURL(
    /oidc\.localhost:8443\/keycloak\/realms\/jellyfin/,
    { timeout: 30_000 },
  );
  await expect(
    page.getByRole("button", { name: "Login with SSO" }),
  ).toHaveCount(0);
  await setPasswordLoginMode(adminPage, "AllowForAllUsers");
  await adminPage.close();
});

test("synchronizes a Jellyfin administrator role after the Identity Provider group changes", async ({
  browser,
  request,
}) => {
  const headers = await providerAdmin(request);
  const user = await providerUser(request, headers);
  const administratorGroup = await providerGroup(
    request,
    headers,
    "jellyfin-admins",
  );

  try {
    const removeGroup = await request.delete(
      `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}/groups/${administratorGroup.id}`,
      { headers },
    );
    expect(removeGroup.ok()).toBe(true);

    const updatedContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const updatedPage = await updatedContext.newPage();
    await signIn(updatedPage);
    const updatedUser = await currentUser(updatedPage);
    expect(updatedUser.Policy.IsAdministrator).toBe(false);
    await updatedContext.close();
  } finally {
    const restoreGroup = await request.put(
      `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}/groups/${administratorGroup.id}`,
      { headers },
    );
    expect(restoreGroup.ok()).toBe(true);
  }
});

test("keeps an Identity Link when the Identity Provider email changes", async ({
  browser,
  page,
  request,
}) => {
  await signIn(page);
  const originalUser = await currentUser(page);
  const headers = await providerAdmin(request);
  const user = await providerUser(request, headers);
  const originalEmail = user.email;
  const changedEmail = "oidc-renamed@example.test";

  try {
    user.email = changedEmail;
    const update = await request.put(
      `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}`,
      { headers, data: user },
    );
    expect(update.ok()).toBe(true);

    const updatedContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const updatedPage = await updatedContext.newPage();
    await signIn(updatedPage);
    const updatedUser = await currentUser(updatedPage);
    expect(updatedUser.Id).toBe(originalUser.Id);
    expect(updatedUser.Name).toBe(changedEmail);
    await updatedContext.close();
  } finally {
    user.email = originalEmail;
    const restore = await request.put(
      `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}`,
      { headers, data: user },
    );
    expect(restore.ok()).toBe(true);
  }
});

test("enforces local password policy through Jellyfin authentication", async ({
  browser,
  request,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  await setPasswordLoginMode(adminPage, "DisableForAllUsers");

  try {
    expect((await authenticatePassword(request, "root", "")).ok()).toBe(false);
  } finally {
    await setPasswordLoginMode(adminPage, "AllowForAllUsers");
    await adminPage.close();
  }
});

test("disables local passwords only for linked Jellyfin Users", async ({
  browser,
  request,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  const user = await currentUser(adminPage);
  const rootLogin = await authenticatePassword(request, "root", "");
  expect(rootLogin.ok()).toBe(true);
  const { AccessToken } = await rootLogin.json();
  const password = "linked-user-password";
  const setPassword = await request.post(
    "https://localhost:8443/Users/Password",
    {
      headers: { Authorization: `MediaBrowser Token="${AccessToken}"` },
      params: { userId: user.Id },
      data: { CurrentPw: "", NewPw: password, ResetPassword: false },
    },
  );
  expect(setPassword.ok()).toBe(true);
  expect((await authenticatePassword(request, user.Name, password)).ok()).toBe(
    true,
  );
  const policyPage = await browser.newPage();
  await signIn(policyPage);
  await setPasswordLoginMode(policyPage, "DisableForLinkedUsersOnly");

  try {
    expect(
      (await authenticatePassword(request, user.Name, password)).ok(),
    ).toBe(false);
    expect((await authenticatePassword(request, "root", "")).ok()).toBe(true);
  } finally {
    await setPasswordLoginMode(policyPage, "AllowForAllUsers");
    await policyPage.close();
    await adminPage.close();
  }
});

test("denies OIDC sign-in when the Identity Provider removes all allowed groups", async ({
  browser,
  request,
}) => {
  const headers = await providerAdmin(request);
  const user = await providerUser(request, headers);
  const groups = await Promise.all([
    providerGroup(request, headers, "jellyfin-users"),
    providerGroup(request, headers, "jellyfin-admins"),
  ]);

  try {
    for (const group of groups) {
      const remove = await request.delete(
        `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}/groups/${group.id}`,
        { headers },
      );
      expect(remove.ok()).toBe(true);
    }

    const deniedContext = await browser.newContext({ ignoreHTTPSErrors: true });
    await signInDenied(await deniedContext.newPage());
    await deniedContext.close();
  } finally {
    for (const group of groups) {
      const restore = await request.put(
        `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}/groups/${group.id}`,
        { headers },
      );
      expect(restore.ok()).toBe(true);
    }
  }
});

test("denies a verified email that belongs to another Jellyfin User", async ({
  browser,
  request,
}) => {
  const rootLogin = await authenticatePassword(request, "root", "");
  expect(rootLogin.ok()).toBe(true);
  const { AccessToken } = await rootLogin.json();
  const email = "oidc-conflict@example.test";
  const created = await request.post("https://localhost:8443/Users/New", {
    headers: { Authorization: `MediaBrowser Token="${AccessToken}"` },
    data: { Name: email, Password: "local-password" },
  });
  expect(created.ok()).toBe(true);
  const localUser = await created.json();
  const headers = await providerAdmin(request);
  const user = await providerUser(request, headers);
  const originalEmail = user.email;

  try {
    user.email = email;
    expect(
      (
        await request.put(
          `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}`,
          { headers, data: user },
        )
      ).ok(),
    ).toBe(true);
    const deniedContext = await browser.newContext({ ignoreHTTPSErrors: true });
    await signInDenied(await deniedContext.newPage());
    await deniedContext.close();
  } finally {
    user.email = originalEmail;
    expect(
      (
        await request.put(
          `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}`,
          { headers, data: user },
        )
      ).ok(),
    ).toBe(true);
    expect(
      (
        await request.delete(`https://localhost:8443/Users/${localUser.Id}`, {
          headers: { Authorization: `MediaBrowser Token="${AccessToken}"` },
        })
      ).ok(),
    ).toBe(true);
  }
});

test("reprovisions a Jellyfin User after its linked user is deleted", async ({
  browser,
  page,
  request,
}) => {
  await signIn(page);
  const user = await currentUser(page);
  const rootLogin = await authenticatePassword(request, "root", "");
  expect(rootLogin.ok()).toBe(true);
  const { AccessToken } = await rootLogin.json();
  expect(
    (
      await request.delete(`https://localhost:8443/Users/${user.Id}`, {
        headers: { Authorization: `MediaBrowser Token="${AccessToken}"` },
      })
    ).ok(),
  ).toBe(true);

  const returningContext = await browser.newContext({
    ignoreHTTPSErrors: true,
  });
  const returningPage = await returningContext.newPage();
  await signIn(returningPage);
  expect((await currentUser(returningPage)).Id).not.toBe(user.Id);
  await returningContext.close();
});

test("returns to Jellyfin login when no logout token is available", async ({
  page,
}) => {
  await page.goto("/oidc/logout");
  await expect(page).toHaveURL(/\/web\/index\.html#!\/login/);
});

test("requests Identity Provider logout when RP-initiated logout is enabled", async ({
  browser,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  const original = await adminPage.evaluate(async () => {
    const config = await ApiClient.getPluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
    );
    const originalValue = config.RpInitiatedLogout;
    config.RpInitiatedLogout = true;
    await ApiClient.updatePluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      config,
    );
    return originalValue;
  });

  try {
    const logoutContext = await browser.newContext({ ignoreHTTPSErrors: true });
    const logoutPage = await logoutContext.newPage();
    await signIn(logoutPage);
    await logoutPage.reload();
    await logoutPage.waitForFunction(
      () => window.oidcRpInitiatedLogout === true,
    );
    const logoutRequest = logoutPage.waitForRequest((request) =>
      request.url().includes("/protocol/openid-connect/logout"),
    );
    await logoutPage.goto("/oidc/logout", { waitUntil: "commit" });
    const request = await logoutRequest;
    const url = new URL(request.url());
    expect(url.searchParams.get("client_id")).toBe("jellyfin");
    expect(url.searchParams.get("id_token_hint")).toBeTruthy();
    expect(url.searchParams.get("post_logout_redirect_uri")).toBe(
      "https://localhost:8443/web/index.html",
    );
    await logoutContext.close();
  } finally {
    await adminPage.evaluate(async (originalValue) => {
      const config = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      config.RpInitiatedLogout = originalValue;
      await ApiClient.updatePluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
        config,
      );
    }, original);
    await adminPage.close();
  }
});
