import { expect, test } from "@playwright/test";

let loginAttempt = 0;

async function signIn(page) {
  await clearJellyfinSession(page);
  await showLogin(page);
  await page
    .getByRole("button", { name: "Sign In with SSO" })
    .click({ noWaitAfter: true });
  const username = page.locator("#username");
  const needsCredentials = await Promise.race([
    username.waitFor({ state: "visible", timeout: 30_000 }).then(() => true),
    page
      .getByRole("button", { name: "User Menu" })
      .waitFor({ state: "visible", timeout: 30_000 })
      .then(() => false),
  ]);
  if (needsCredentials) {
    await username.fill("oidc-test");
    await page.locator("#password").fill("oidc-test-password");
    await page.locator("#kc-login").click({ noWaitAfter: true });
  }
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
  await clearJellyfinSession(page);
  await showLogin(page);
  await page
    .getByRole("button", { name: "Sign In with SSO" })
    .click({ noWaitAfter: true });
  const username = page.locator("#username");
  await username.waitFor({ state: "visible", timeout: 30_000 });
  await username.fill("oidc-test");
  await page.locator("#password").fill("oidc-test-password");
  await page.locator("#kc-login").click({ noWaitAfter: true });
  await expect(page).toHaveURL(/oidcError=1/, { timeout: 30_000 });
}

async function setConfigurationValue(page, property, value) {
  return page.evaluate(
    async ([property, value]) => {
      const config = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      const originalValue = config[property];
      config[property] = value;
      await ApiClient.updatePluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
        config,
      );
      return originalValue;
    },
    [property, value],
  );
}

async function setPasswordLoginMode(page, mode) {
  await setConfigurationValue(page, "PasswordLoginMode", mode);
}

async function showLogin(page) {
  loginAttempt += 1;
  await page.goto(`/web/index.html?oidcTest=${loginAttempt}#!/login`);
}

async function clearJellyfinSession(page) {
  await page.goto("/web/index.html");
  await page.evaluate(() => {
    localStorage.removeItem("jellyfin_credentials");
    sessionStorage.clear();
  });
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

async function updateProviderUser(request, headers, user) {
  return request.put(
    `https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/${user.id}`,
    { headers, data: user },
  );
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

  const original = await setConfigurationValue(
    page,
    "SynchronizeProfileImages",
    true,
  );

  try {
    const profileImageContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const profileImagePage = await profileImageContext.newPage();
    await signIn(profileImagePage);
    const imageTag = (await currentUser(profileImagePage)).PrimaryImageTag;
    expect(imageTag).toBeTruthy();
    await profileImageContext.close();

    const unchangedImageContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const unchangedImagePage = await unchangedImageContext.newPage();
    await signIn(unchangedImagePage);
    expect((await currentUser(unchangedImagePage)).PrimaryImageTag).toBe(
      imageTag,
    );
    await unchangedImageContext.close();
  } finally {
    await setConfigurationValue(page, "SynchronizeProfileImages", original);
  }
});

test("shows an OIDC sign-in error on the login page", async ({ page }) => {
  await page.goto("/web/index.html#!/login?oidcError=1");
  await expect(page.locator(".toast")).toHaveText(
    "We couldn't sign you in. Please try again.",
  );
});

test("restores the SSO button after returning from the Identity Provider", async ({
  page,
}) => {
  await showLogin(page);
  const button = page.getByRole("button", { name: "Sign In with SSO" });
  await button.click({ noWaitAfter: true });
  await expect(page).toHaveURL(/oidc\.localhost:8443/, { timeout: 30_000 });
  await page.goBack();
  await expect(button).toBeEnabled();
  await expect(button).toHaveAccessibleName("Sign In with SSO");
});

test("loads the seeded OIDC settings and validates changes for an administrator", async ({
  page,
}) => {
  await signIn(page);
  await expect(page.locator('script[src*="/oidc/web.js?v="]')).toHaveCount(1);
  const configResponse = await page.request.get("/oidc/config");
  expect(configResponse.headers()["cache-control"]).toContain("no-store");
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
  await expect(page.locator("#RedirectSignInPageToProvider")).not.toBeChecked();
  await expect(
    page.locator("#RedirectSignInPageToProviderContainer"),
  ).toBeHidden();
  await expect(page.locator("#SaveButton")).toBeDisabled();
  expect(
    await page.locator("#ClientSecretToggle").evaluate((button) => {
      const input = document.querySelector("#ClientSecret");
      return (
        input &&
        button.getBoundingClientRect().left >=
          input.getBoundingClientRect().right
      );
    }),
  ).toBe(true);
  await expect(page.locator("#ClientSecret")).toHaveAttribute(
    "type",
    "password",
  );
  await page.locator("#ClientSecretToggle").click();
  await expect(page.locator("#ClientSecret")).toHaveAttribute("type", "text");
  await page.locator("#ClientSecretToggle").click();
  await expect(page.locator("#ClientSecret")).toHaveAttribute(
    "type",
    "password",
  );

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

  await page.locator("#IssuerUrl").fill("http://identity.example.test");
  await page.locator("#SaveButton").click();
  expect(
    await page
      .locator("#IssuerUrl")
      .evaluate((input) => input.validity.customError),
  ).toBe(true);

  await page
    .locator("#IssuerUrl")
    .fill("https://oidc.localhost:8443/keycloak/realms/jellyfin");
  await expect(page.locator("#SaveButton")).toBeDisabled();

  await page.locator("#PublicUrl").fill("http://jellyfin.example.test");
  await page.locator("#SaveButton").click();
  expect(
    await page
      .locator("#PublicUrl")
      .evaluate((input) => input.validity.customError),
  ).toBe(true);
  await page.locator("#PublicUrl").fill("https://localhost:8443");
  await expect(page.locator("#SaveButton")).toBeDisabled();

  await page
    .locator("#PasswordLoginMode")
    .selectOption("DisableForLinkedUsersOnly");
  await expect(
    page.locator("#RedirectSignInPageToProviderContainer"),
  ).toBeHidden();
  await page.locator("#SaveButton").click();
  await expect(
    page.getByRole("heading", { name: "You could be locked out" }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Local password sign-in will be disabled for linked users. Verify OIDC sign-in is working before saving.",
    ),
  ).toBeVisible();
  await page.getByRole("button", { name: "Cancel" }).click();
});

test("uses OIDC settings saved without restarting Jellyfin", async ({
  page,
}) => {
  await signIn(page);
  const original = await setConfigurationValue(
    page,
    "ClientId",
    "updated-client-id",
  );

  try {
    const response = await page.request.get("/oidc/start", {
      maxRedirects: 0,
    });
    expect(response.status()).toBe(500);
  } finally {
    await setConfigurationValue(page, "ClientId", original);
  }

  expect(
    (
      await page.request.get("/oidc/start", {
        maxRedirects: 0,
      })
    ).status(),
  ).toBe(302);
});

test("localizes the OIDC configuration page", async ({ page }) => {
  await signIn(page);
  await page.getByRole("button", { name: "User Menu" }).click();
  await page.getByRole("menuitem", { name: "Settings" }).click();
  await page.getByRole("link", { name: "Display" }).click();
  await page.getByRole("combobox", { name: /Display language/ }).click();
  await page.getByRole("option", { name: "Deutsch" }).click();
  const saved = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      /\/Users\/[^/]+\/Configuration$/.test(new URL(response.url()).pathname),
  );
  await page.getByRole("button", { name: "Save" }).click();
  await saved;
  await page.reload();
  await page.waitForFunction(() => document.documentElement.lang === "de");

  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );

  await expect(page.getByRole("heading", { name: "Verbindung" })).toBeVisible();
  await expect(page.locator('label[for="IssuerUrl"]')).toHaveText(
    "Issuer-URL *",
  );
  await expect(
    page.getByText("Zugelassene Gruppen", { exact: true }),
  ).toBeVisible();
});

test("shows OIDC-only controls and redirects root visits when local passwords are disabled for all users", async ({
  browser,
  page,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  await adminPage.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(adminPage.locator("#Enabled")).toBeChecked({ timeout: 30_000 });
  const restorePage = await browser.newPage();
  await signIn(restorePage);

  try {
    await setPasswordLoginMode(adminPage, "AllowForAllUsers");
    await showLogin(page);
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(page.locator(".btnQuick")).toBeVisible();

    await setPasswordLoginMode(adminPage, "DisableForLinkedUsersOnly");
    await showLogin(page);
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(page.locator(".btnQuick")).toBeVisible();

    await setPasswordLoginMode(adminPage, "DisableForAllUsers");
    const response = await adminPage.request.get("/web/index.html", {
      maxRedirects: 0,
    });
    expect(response.status()).toBe(200);
    await showLogin(page);
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(
      page.getByRole("heading", { name: "Please sign in" }),
    ).toBeVisible();
    await expect(page.locator(".visualLoginForm")).toHaveCount(1);
    await expect(page.locator(".manualLoginForm")).toHaveCount(0);
    await expect(page.locator("#divUsers")).toHaveCount(0);
    await expect(page.locator(".readOnlyContent")).toHaveCount(1);
    await expect(page.locator(".readOnlyContent > *")).toHaveCount(4);
    await expect(page.locator(".btnManual")).toHaveCount(0);
    await expect(page.locator(".btnForgotPassword")).toHaveCount(0);
    await expect(page.locator(".btnQuick")).toBeVisible();
    await expect(page.locator(".btnSelectServer")).toBeHidden();
    await expect(page.locator(".loginDisclaimerContainer")).toHaveCount(1);
    await page.locator(".btnQuick").click();
    await expect(page.locator("#quickConnectAlert")).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).not.toHaveClass(/cancel/);
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toHaveClass(/button-submit/);

    await setConfigurationValue(
      adminPage,
      "RedirectSignInPageToProvider",
      true,
    );
    const directResponse = await adminPage.request.get("/web/index.html", {
      maxRedirects: 0,
    });
    expect(directResponse.status()).toBe(200);
    expect(await directResponse.text()).toContain(
      'id="oidc-login-redirect-style"',
    );
    await page.addInitScript(() => {
      const captureLoginRender = () => {
        const login = document.querySelector("#loginPage");
        if (login) {
          sessionStorage.setItem("oidcLoginRendered", "true");
        }
      };
      new MutationObserver(captureLoginRender).observe(
        document.documentElement,
        {
          childList: true,
          subtree: true,
        },
      );
    });
    await page.route("**/oidc/config", (route) => route.abort());
    await page.route("**/oidc/start*", (route) =>
      route.fulfill({ contentType: "text/html", body: "" }),
    );
    const startRequest = page.waitForRequest(
      (request) => new URL(request.url()).pathname === "/oidc/start",
    );
    await page.goto("/");
    await startRequest;
    await page.waitForURL("**/oidc/start*");
    await expect
      .poll(() =>
        page.evaluate(() => sessionStorage.getItem("oidcLoginRendered")),
      )
      .toBeNull();
    await page.unroute("**/oidc/start*");
    await page.unroute("**/oidc/config");
    let redirectedActiveSession = false;
    const captureOidcStart = (request) => {
      redirectedActiveSession ||=
        new URL(request.url()).pathname === "/oidc/start";
    };
    adminPage.on("request", captureOidcStart);
    await adminPage.reload();
    await adminPage.waitForFunction(
      () => window.oidcRedirectSignInPageToProvider === true,
      { timeout: 30_000 },
    );
    adminPage.off("request", captureOidcStart);
    expect(redirectedActiveSession).toBe(false);
    await adminPage.getByRole("button", { name: "User Menu" }).click();
    await adminPage.getByRole("menuitem", { name: "Sign Out" }).click();
    await expect(adminPage).toHaveURL(/oidcSignedOut=1/);
    await expect(
      adminPage.getByRole("heading", { name: "Signed Out" }),
    ).toBeVisible();
    await expect(
      adminPage.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(
      adminPage.getByRole("button", { name: "Sign In with SSO" }),
    ).toHaveClass(/button-submit/);
    await expect(adminPage.locator(".visualLoginForm")).toHaveCount(1);
    await expect(adminPage.locator(".readOnlyContent")).toHaveCount(1);
    await expect(adminPage.locator(".manualLoginForm")).toHaveCount(0);
  } finally {
    await setConfigurationValue(
      restorePage,
      "RedirectSignInPageToProvider",
      false,
    );
    await setPasswordLoginMode(restorePage, "AllowForAllUsers");
    await restorePage.close();
    await adminPage.close();
  }
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
    const update = await updateProviderUser(request, headers, user);
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
    const restore = await updateProviderUser(request, headers, user);
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
    expect((await updateProviderUser(request, headers, user)).ok()).toBe(true);
    const deniedContext = await browser.newContext({ ignoreHTTPSErrors: true });
    await signInDenied(await deniedContext.newPage());
    await deniedContext.close();
  } finally {
    user.email = originalEmail;
    expect((await updateProviderUser(request, headers, user)).ok()).toBe(true);
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
  expect(
    await returningPage.evaluate(async () => {
      const config = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      return config.IdentityLinks.length;
    }),
  ).toBe(1);
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
  const original = await setConfigurationValue(
    adminPage,
    "RpInitiatedLogout",
    true,
  );

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
    await setConfigurationValue(adminPage, "RpInitiatedLogout", original);
    await adminPage.close();
  }
});

test("leaves the post-logout page to the Identity Provider when local passwords are disabled", async ({
  browser,
}) => {
  const adminPage = await browser.newPage();
  await signIn(adminPage);
  const logoutContext = await browser.newContext({ ignoreHTTPSErrors: true });
  const logoutPage = await logoutContext.newPage();
  await signIn(logoutPage);
  const originalRpLogout = await setConfigurationValue(
    adminPage,
    "RpInitiatedLogout",
    true,
  );
  const originalPasswordMode = await setConfigurationValue(
    adminPage,
    "PasswordLoginMode",
    "DisableForAllUsers",
  );
  const originalRedirect = await setConfigurationValue(
    adminPage,
    "RedirectSignInPageToProvider",
    true,
  );

  try {
    const logoutRequest = logoutPage.waitForRequest((request) =>
      request.url().includes("/protocol/openid-connect/logout"),
    );
    await logoutPage.goto("/oidc/logout", { waitUntil: "commit" });
    const url = new URL((await logoutRequest).url());
    expect(url.searchParams.get("client_id")).toBe("jellyfin");
    expect(url.searchParams.get("id_token_hint")).toBeTruthy();
    expect(url.searchParams.has("post_logout_redirect_uri")).toBe(false);
  } finally {
    await setConfigurationValue(
      adminPage,
      "PasswordLoginMode",
      originalPasswordMode,
    );
    await setConfigurationValue(
      adminPage,
      "RpInitiatedLogout",
      originalRpLogout,
    );
    await setConfigurationValue(
      adminPage,
      "RedirectSignInPageToProvider",
      originalRedirect,
    );
    await logoutContext.close();
    await adminPage.close();
  }
});
