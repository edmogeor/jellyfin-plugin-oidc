import { expect, test } from "@playwright/test";
import { execFileSync } from "node:child_process";

const abyssTheme = process.env.ABYSS_THEME === "1";
const elegantFinTheme = process.env.ELEGANTFIN_THEME === "1";
let loginAttempt = 0;

async function signIn(page, providerUsername = "user") {
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
    await username.fill(providerUsername);
    await page.locator("#password").fill("password");
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

async function signInDenied(page, providerUsername = "user") {
  await clearJellyfinSession(page);
  await showLogin(page);
  await page
    .getByRole("button", { name: "Sign In with SSO" })
    .click({ noWaitAfter: true });
  const username = page.locator("#username");
  await username.waitFor({ state: "visible", timeout: 30_000 });
  await username.fill(providerUsername);
  await page.locator("#password").fill("password");
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
      params: { username: "user", exact: "true" },
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

async function localSignIn(page, username, password, layout = "modern") {
  await clearJellyfinSession(page);
  await page.evaluate(
    (layout) => localStorage.setItem("layout", layout),
    layout,
  );
  await showLogin(page);
  await page.getByRole("textbox", { name: "User", exact: true }).fill(username);
  await page
    .getByRole("textbox", { name: "Password", exact: true })
    .fill(password);
  await page.getByRole("button", { name: "Sign In", exact: true }).click();
  await page.waitForFunction(
    () =>
      window.ApiClient?.accessToken() && window.ApiClient?.getCurrentUserId(),
  );
  await expect(page).toHaveURL(/#!?\/home/);
}

async function restartJellyfin(request, readyPath, headers) {
  execFileSync("docker", ["compose", "restart", "jellyfin-server"], {
    cwd: new URL("../", import.meta.url),
    stdio: "pipe",
  });
  await expect
    .poll(
      async () =>
        request
          .get(readyPath, { headers })
          .then((response) => response.status())
          .catch(() => 0),
      { timeout: 60_000 },
    )
    .toBe(200);
}

test("provisions the allowed OIDC test user and starts a Jellyfin session", async ({
  page,
}) => {
  await signIn(page);

  const user = await currentUser(page);

  expect(user.Name).toBe("user");
  expect(user.Policy.IsAdministrator).toBe(true);
  expect((await authenticatePassword(page.request, user.Name, "")).ok()).toBe(
    false,
  );
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
  const error = page.locator("[data-oidc-error]");
  await expect(error).toHaveText("We couldn't sign you in. Please try again.");
  await expect(error).toHaveCount(0, { timeout: 5_000 });
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
  await expect(page.locator("#UsernameClaim")).toHaveValue(
    "preferred_username",
  );
  await expect(page.locator("#RequireVerifiedEmail")).toBeChecked();
  await expect(page.locator("#AdministratorGroup")).toHaveValue(
    "jellyfin-admins",
  );
  await expect(page.locator("#SynchronizeProfileImages")).not.toBeChecked();
  await expect(page.locator("#RedirectSignInPageToProvider")).not.toBeChecked();
  await expect(
    page.locator("#RedirectSignInPageToProviderContainer"),
  ).toBeHidden();
  await expect(page.locator("#SaveButton")).toBeDisabled();
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

test("saves the verified-email requirement and username claim without renaming existing users", async ({
  page,
}) => {
  await signIn(page);
  const originalUser = await currentUser(page);
  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(page.locator("#RequireVerifiedEmail")).toBeChecked();
  await expect(page.locator("#UsernameClaim")).toHaveValue(
    "preferred_username",
  );
  try {
    await page.getByText("Require verified email", { exact: true }).click();
    await expect(page.locator("#RequireVerifiedEmail")).not.toBeChecked();
    await page.locator("#UsernameClaim").fill("email");
    await page.locator("#AutomaticLinkingMode").selectOption("None");
    await page
      .locator("#ProvisioningUsernameMode")
      .selectOption("VerifiedEmail");
    const saved = page.waitForResponse(
      (response) =>
        response.request().method() === "POST" &&
        response
          .url()
          .includes(
            "/Plugins/4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4/Configuration",
          ),
    );
    await page.locator("#SaveButton").click();
    expect((await saved).ok()).toBe(true);
    await expect(page.locator("#SaveButton")).toBeDisabled();
    await page.reload();
    await expect(page.locator("#RequireVerifiedEmail")).not.toBeChecked();
    await expect(page.locator("#UsernameClaim")).toHaveValue("email");
    await expect(page.locator("#AutomaticLinkingMode")).toHaveValue("None");
    await expect(page.locator("#ProvisioningUsernameMode")).toHaveValue(
      "VerifiedEmail",
    );
    await page
      .locator("#ProvisioningUsernameMode")
      .selectOption("UsernameClaim");
    for (const mode of ["UsernameToUsername", "EmailToUsername"]) {
      await page.locator("#AutomaticLinkingMode").selectOption(mode);
      const response = page.waitForResponse(
        (response) =>
          response.request().method() === "POST" &&
          response
            .url()
            .includes(
              "/Plugins/4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4/Configuration",
            ),
      );
      await page.locator("#SaveButton").click();
      expect((await response).ok()).toBe(true);
      await page.reload();
      await expect(page.locator("#AutomaticLinkingMode")).toHaveValue(mode);
    }
    expect((await currentUser(page)).Name).toBe(originalUser.Name);
  } finally {
    await setConfigurationValue(page, "UsernameClaim", "preferred_username");
    await setConfigurationValue(
      page,
      "ProvisioningUsernameMode",
      "UsernameClaim",
    );
    await setConfigurationValue(page, "RequireVerifiedEmail", true);
    await setConfigurationValue(
      page,
      "AutomaticLinkingMode",
      "EmailToUsername",
    );
  }
});

test("provisions new usernames from verified email without changing provider metadata", async ({
  browser,
  page,
  request,
}) => {
  await localSignIn(page, "root", "");
  const headers = {
    Authorization: `MediaBrowser Token="${await page.evaluate(() => ApiClient.accessToken())}"`,
  };
  const originalMode = await setConfigurationValue(
    page,
    "ProvisioningUsernameMode",
    "VerifiedEmail",
  );
  const providerHeaders = await providerAdmin(request);
  const name = `email-enrollment-${Date.now()}`;
  const email = `${name}@example.test`;
  const created = await request.post(
    "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users",
    {
      headers: providerHeaders,
      data: {
        username: name,
        email,
        emailVerified: true,
        firstName: "Email",
        lastName: "Enrollment",
        enabled: true,
        groups: ["/jellyfin-users"],
        credentials: [
          { type: "password", value: "password", temporary: false },
        ],
      },
    },
  );
  expect(created.ok()).toBe(true);
  const providerLocation = created.headers().location;
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  let user;
  try {
    const signup = await context.newPage();
    await signIn(signup, name);
    user = await currentUser(signup);
    expect(user.Name).toBe(email);
    const state = await (
      await request.get(`/oidc/admin/users/${user.Id}`, { headers })
    ).json();
    expect(state.Username).toBe(name);
    expect(state.Email).toBe(email);
    await setConfigurationValue(
      page,
      "ProvisioningUsernameMode",
      "UsernameClaim",
    );
    await signIn(signup, name);
    expect((await currentUser(signup)).Name).toBe(email);
  } finally {
    await setConfigurationValue(page, "ProvisioningUsernameMode", originalMode);
    if (user) {
      const state = await (
        await request.get(`/oidc/admin/users/${user.Id}`, { headers })
      ).json();
      expect(
        (
          await request.delete(
            `/oidc/admin/users/${user.Id}/link?version=${state.Version}`,
            { headers },
          )
        ).ok(),
      ).toBe(true);
      await request.delete(`/Users/${user.Id}`, { headers });
    }
    await context.close();
    await request.delete(providerLocation, { headers: providerHeaders });
  }
});

test("keeps saved configuration visible when an older load finishes late", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(page.locator("#ClientId")).toHaveValue("jellyfin");
  await expect(page.locator("#SaveButton")).toBeDisabled();
  let releaseLoad;
  let signalLoad;
  const heldLoad = new Promise((resolve) => {
    releaseLoad = resolve;
  });
  const loadStarted = new Promise((resolve) => {
    signalLoad = resolve;
  });
  let holdNextLoad = true;
  const configurationUrl =
    "**/Plugins/4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4/Configuration";
  await page.route(configurationUrl, async (route) => {
    if (route.request().method() !== "GET" || !holdNextLoad) {
      return route.continue();
    }
    holdNextLoad = false;
    const response = await route.fetch();
    signalLoad();
    await heldLoad;
    return route.fulfill({ response });
  });

  try {
    await page.evaluate(() =>
      document
        .querySelector("#OidcConfigPage")
        .dispatchEvent(new Event("pageshow")),
    );
    await loadStarted;
    await page.locator("#LoginButtonText").fill("Saved UI label");
    const saved = page.waitForResponse(
      (response) =>
        response.request().method() === "POST" &&
        response.url().endsWith("/Configuration"),
    );
    await page.locator("#SaveButton").click();
    expect((await saved).ok()).toBe(true);
    await expect(page.locator("#SaveButton")).toBeDisabled();
    const oldLoadFinished = page.waitForResponse(
      (response) =>
        response.request().method() === "GET" &&
        response.url().endsWith("/Configuration"),
    );
    releaseLoad();
    await (await oldLoadFinished).finished();
    await expect(page.locator("#LoginButtonText")).toHaveValue(
      "Saved UI label",
    );
    await expect(page.locator("#SaveButton")).toBeDisabled();
    await page.reload();
    await expect(page.locator("#LoginButtonText")).toHaveValue(
      "Saved UI label",
    );
  } finally {
    releaseLoad();
    await page.unroute(configurationUrl);
    await setConfigurationValue(page, "LoginButtonText", "Sign In with SSO");
  }
});

test("shows configuration save failures and allows retrying the draft", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(page.locator("#ClientId")).toHaveValue("jellyfin");
  const configurationUrl =
    "**/Plugins/4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4/Configuration";
  await page.route(configurationUrl, (route) =>
    route.request().method() === "POST"
      ? route.fulfill({
          status: 500,
          headers: { "X-Application-Error-Code": "Injected save failure" },
        })
      : route.continue(),
  );
  try {
    await page.locator("#LoginButtonText").fill("Retry UI label");
    await page.locator("#SaveButton").click();
    await expect(
      page.getByText("Injected save failure", { exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Got It", exact: true }).click();
    await expect(page.locator("#LoginButtonText")).toHaveValue(
      "Retry UI label",
    );
    await expect(page.locator("#SaveButton")).toBeEnabled();
    await page.unroute(configurationUrl);
    const saved = page.waitForResponse(
      (response) =>
        response.request().method() === "POST" &&
        response.url().endsWith("/Configuration"),
    );
    await page.locator("#SaveButton").click();
    expect((await saved).ok()).toBe(true);
    await expect(page.locator("#SaveButton")).toBeDisabled();
    await page.reload();
    await expect(page.locator("#LoginButtonText")).toHaveValue(
      "Retry UI label",
    );
  } finally {
    await page.unroute(configurationUrl);
    await setConfigurationValue(page, "LoginButtonText", "Sign In with SSO");
  }
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

test("loads configuration translations after a temporary failure", async ({
  page,
}) => {
  await showLogin(page);
  await page.getByRole("textbox", { name: "User" }).fill("root");
  await page.getByRole("button", { name: "Sign In", exact: true }).click();
  await expect(page.getByRole("button", { name: "User Menu" })).toBeVisible();
  let requests = 0;
  await page.route("**/oidc/strings/*", (route) => {
    if (++requests === 1) return route.fulfill({ status: 503 });
    return route.continue();
  });

  await page.goto(
    "/web/index.html#/configurationpage?name=OIDC%20Authentication",
  );
  await expect(page.locator("#OidcConfigPage h1")).toHaveText(
    "OIDC Authentication",
    { timeout: 10_000 },
  );
  await expect.poll(() => requests).toBeGreaterThan(1);
  await page.evaluate(() => {
    const oidcPage = document.querySelector("#OidcConfigPage");
    const otherPage = document.createElement("div");
    otherPage.dataset.i18n = "unrelatedTranslationKey";
    oidcPage.before(otherPage);
    oidcPage.querySelector("h1").textContent = "";
    oidcPage.dispatchEvent(new Event("pageshow"));
  });
  await expect(page.locator("#OidcConfigPage h1")).toHaveText(
    "OIDC Authentication",
  );
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
    await expect(page.locator(".visualLoginForm")).toHaveCount(1);
    const oidcForm = page.locator(
      "form.manualLoginForm[data-oidc-login-container]",
    );
    await expect(oidcForm).toBeVisible();
    if (abyssTheme) {
      await expect(oidcForm).toHaveCSS("padding-top", "24px");
    }
    await expect(oidcForm.locator("[data-oidc-login]")).toHaveCount(1);
    await expect(oidcForm.locator('button[type="submit"]')).toBeHidden();
    await expect(page.locator(".visualLoginForm")).toBeHidden();
    await expect(page.locator("#divUsers")).toBeHidden();
    await expect(page.locator(".btnManual")).toBeHidden();
    await expect(page.locator(".btnForgotPassword")).toBeHidden();
    await expect(page.locator(".btnQuick")).toBeVisible();
    await expect(page.locator(".btnSelectServer")).toBeHidden();
    await page.getByRole("banner").getByRole("link").click();
    await expect(page).toHaveURL(/login\?serverid=/);
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(page.locator(".btnQuick:visible")).toBeVisible();
    await page.locator(".btnQuick:visible").click();
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
    for (const mode of [
      "AllowForAllUsers",
      "DisableForLinkedUsersOnly",
      "DisableForAllUsers",
    ]) {
      await setPasswordLoginMode(adminPage, mode);
      expect(
        (await (await adminPage.request.get("/oidc/config")).json())
          .RedirectSignInPageToProvider,
      ).toBe(mode === "DisableForAllUsers");
    }
    const directResponse = await adminPage.request.get("/web/index.html", {
      maxRedirects: 0,
    });
    expect(directResponse.status()).toBe(200);
    const directHtml = await directResponse.text();
    expect(directHtml).toContain('id="oidc-login-redirect-style"');
    expect(directHtml).toContain("#loginPage{visibility:hidden}");
    const webResponse = await adminPage.request.get("/oidc/web.js");
    expect(await webResponse.text()).toContain("location.assign(endpoint())");
    const errorResponse = await adminPage.request.get(
      "/web/index.html?oidcError=1",
    );
    const errorHtml = await errorResponse.text();
    expect(errorHtml).not.toContain('id="oidc-login-redirect-style"');
    const ticketResponse = await adminPage.request.get(
      "/web/index.html?oidcTicket=invalid",
    );
    expect(await ticketResponse.text()).not.toContain(
      'id="oidc-only-login-style"',
    );
    await page.goto("/web/index.html?oidcError=1#!/login");
    await expect(
      page.getByRole("button", { name: "Sign In with SSO" }),
    ).toHaveClass(/button-submit/);
    await page.route("**/oidc/config", (route) => route.abort());
    let startRequests = 0;
    await page.route("**/oidc/start*", (route) => {
      startRequests += 1;
      return route.abort();
    });
    await page.goto("/");
    await expect.poll(() => startRequests).toBe(1);
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(startRequests).toBe(1);
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
    const serverId = await adminPage.evaluate(
      () =>
        JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0].Id,
    );
    await adminPage.getByRole("button", { name: "User Menu" }).click();
    await adminPage.getByRole("menuitem", { name: "Sign Out" }).click();
    await expect(adminPage).toHaveURL(/oidcSignedOut=1/);
    if (elegantFinTheme) {
      expect(
        await adminPage
          .locator("form.manualLoginForm[data-oidc-login-container]")
          .evaluate((form) => getComputedStyle(form, "::before").content),
      ).toBe('"Signed Out"');
    } else {
      await expect(
        adminPage.getByRole("heading", { name: "Signed Out" }),
      ).toBeVisible();
    }
    await expect(
      adminPage.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    await expect(
      adminPage.getByRole("button", { name: "Sign In with SSO" }),
    ).toHaveClass(/button-submit/);
    const pageErrors = [];
    adminPage.on("pageerror", (error) => pageErrors.push(error.message));
    await expect
      .poll(() =>
        adminPage.evaluate(
          () =>
            JSON.parse(localStorage.getItem("jellyfin_credentials")).Servers[0],
        ),
      )
      .toMatchObject({ Id: serverId, AccessToken: null, UserId: null });
    await adminPage.evaluate((id) => {
      location.hash = `/login?serverid=${id}&url=%2Fhome`;
    }, serverId);
    await expect
      .poll(() => new URL(adminPage.url()).hash)
      .toBe(`#/login?serverid=${serverId}&url=%2Fhome`);
    if (!elegantFinTheme) {
      await expect(
        adminPage.getByRole("heading", { name: "Signed Out" }),
      ).toBeVisible();
    }
    await expect(
      adminPage.getByRole("button", { name: "Sign In with SSO" }),
    ).toBeVisible();
    expect(pageErrors).toEqual([]);
    await expect(adminPage.locator(".visualLoginForm")).toBeHidden();
    await expect(adminPage.locator(".readOnlyContent")).toHaveCount(1);
    await expect(
      adminPage.locator("form.manualLoginForm[data-oidc-login-container]"),
    ).toBeVisible();
    await expect(adminPage.locator("#divUsers")).toBeHidden();
    await expect(adminPage.locator(".btnQuick")).toBeHidden();
    await expect(adminPage.locator(".btnSelectServer")).toBeHidden();
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

for (const layout of ["modern", "desktop-legacy"]) {
  test(`manages approvals and OIDC-only sign-out using native ${layout} editor components`, async ({
    browser,
    page,
    request,
  }) => {
    test.setTimeout(240_000);
    await localSignIn(page, "root", "", layout);
    const rootToken = await page.evaluate(() => ApiClient.accessToken());
    const headers = { Authorization: `MediaBrowser Token="${rootToken}"` };
    const originalMode = await setConfigurationValue(
      page,
      "PasswordLoginMode",
      "AllowForAllUsers",
    );
    const name = `admin-managed-${layout}-${Date.now()}`;
    const created = await request.post("/Users/New", {
      headers,
      data: { Name: name, Password: "local-password" },
    });
    expect(created.ok()).toBe(true);
    const target = await created.json();
    const path = `/oidc/admin/users/${target.Id}`;
    const status = async () => {
      let response;
      await expect
        .poll(
          async () => {
            response = await request.get(path, { headers });
            return response.status();
          },
          { timeout: 60_000 },
        )
        .toBe(200);
      expect(
        response.ok(),
        `Status endpoint returned ${response.status()}`,
      ).toBe(true);
      expect(response.headers()["cache-control"]).toContain("no-store");
      return response.json();
    };
    const providerHeaders = await providerAdmin(request);
    const providerCreated = await request.post(
      "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users",
      {
        headers: providerHeaders,
        data: {
          username: name,
          email: `${name}@example.test`,
          emailVerified: true,
          firstName: "Admin",
          lastName: "Test",
          enabled: true,
          groups: ["/jellyfin-users"],
          credentials: [
            { type: "password", value: "password", temporary: false },
          ],
        },
      },
    );
    expect(providerCreated.ok()).toBe(true);
    const providerLocation = providerCreated.headers().location;
    const subject = providerLocation.split("/").at(-1);
    const local = await (
      await authenticatePassword(request, name, "local-password")
    ).json();
    const localHeaders = {
      Authorization: `MediaBrowser Token="${local.AccessToken}"`,
    };
    const qc = await request.post("/QuickConnect/Initiate", {
      headers: {
        Authorization: `MediaBrowser Client="e2e", Device="Quick Connect", DeviceId="quick-${layout}", Version="1"`,
      },
    });
    expect(qc.ok()).toBe(true);
    const quick = await qc.json();
    expect(
      (
        await request.post(`/QuickConnect/Authorize?code=${quick.Code}`, {
          headers: localHeaders,
        })
      ).ok(),
    ).toBe(true);
    const qcAuth = await request.post("/Users/AuthenticateWithQuickConnect", {
      data: { Secret: quick.Secret },
    });
    expect(qcAuth.ok()).toBe(true);
    const qcToken = (await qcAuth.json()).AccessToken;
    const contexts = [];
    const oidcTokens = [];
    const tokenStatus = (token) =>
      request
        .get("/Users/Me", {
          headers: { Authorization: `MediaBrowser Token="${token}"` },
        })
        .then((response) => response.status());
    const editor = `/web/index.html#/dashboard/users/${target.Id}/profile?oidc=1`;
    try {
      for (const [method, suffix, data] of [
        ["get", "", null],
        [
          "put",
          "/link-request",
          { Version: "00000000-0000-0000-0000-000000000000", Subject: subject },
        ],
        ["delete", "/link-request", null],
        ["delete", "/link", null],
        [
          "post",
          "/retry-sign-out",
          { Operation: "00000000-0000-0000-0000-000000000000" },
        ],
      ]) {
        expect([401, 403]).toContain(
          (
            await request[method](path + suffix, {
              headers: localHeaders,
              ...(data ? { data } : {}),
            })
          ).status(),
        );
        expect([401, 403]).toContain(
          (await request[method](path + suffix, data ? { data } : {})).status(),
        );
      }
      await page.route(`**${path}`, (route) => route.fulfill({ status: 503 }));
      await page.goto(editor);
      const panel = page.getByRole("tabpanel", { name: "OIDC" });
      await expect(
        page.getByRole("tab", { name: "OIDC", exact: true }),
      ).toHaveCount(1);
      await expect(panel).toContainText("Unable to check link status.");
      await expect(panel).not.toContainText("Not linked");
      await page.unroute(`**${path}`);
      await panel.getByRole("button", { name: "Retry", exact: true }).click();
      await expect(panel).toContainText("Not linked");
      await page.getByRole("tab", { name: "Password", exact: true }).click();
      await expect(
        page.getByRole("button", { name: "Save Password" }),
      ).toBeVisible();
      await page.goBack();
      await expect(
        page.getByRole("tab", { name: "OIDC", exact: true }),
      ).toHaveAttribute("aria-selected", "true");
      await page.getByRole("tab", { name: "OIDC", exact: true }).focus();
      await page.keyboard.press("ArrowLeft");
      await expect(
        page.getByRole("tab", { name: "Password", exact: true }),
      ).toBeFocused();
      await page.keyboard.press("ArrowRight");
      await expect(
        page.getByRole("tab", { name: "OIDC", exact: true }),
      ).toBeFocused();
      await panel.getByRole("button", { name: "Link on next sign-in" }).click();
      await expect(
        panel.getByRole("textbox", { name: "Value", exact: true }),
      ).toHaveClass(/emby-input/);
      await panel
        .getByRole("textbox", { name: "Value", exact: true })
        .fill(` ${name}@example.test `);
      await expect(
        panel.getByRole("combobox", { name: "Match by" }),
      ).toHaveValue("Email");
      await page.screenshot({
        path: test.info().outputPath("admin-form-desktop.png"),
        fullPage: true,
      });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({
        path: test.info().outputPath("admin-form-mobile.png"),
        fullPage: true,
      });
      await page.setViewportSize({ width: 1280, height: 720 });
      await panel.getByRole("button", { name: "Approve link" }).click();
      await expect(panel).toContainText(
        "Waiting for Identity Provider sign-in",
      );
      await page.evaluate(() => {
        document.documentElement.lang = "de";
      });
      await page.getByRole("tab", { name: "Password", exact: true }).click();
      await page.getByRole("tab", { name: "OIDC", exact: true }).click();
      await expect(panel).toContainText(
        "Anmeldung beim Identitätsanbieter ausstehend",
      );
      await expect(
        panel.getByRole("button", { name: "Genehmigung bearbeiten" }),
      ).toBeVisible();
      await page.route("**/oidc/strings/*", (route) =>
        route.fulfill({ status: 503 }),
      );
      await page.reload();
      await expect(panel).toContainText(
        "Waiting for Identity Provider sign-in",
      );
      await expect(
        panel.getByRole("button", { name: "Edit approval" }),
      ).toBeVisible();
      await page.unroute("**/oidc/strings/*");
      const staleApproval = await status();
      await page.route(`**${path}`, (route) =>
        route.fulfill({ json: { ...staleApproval, PartialRevocation: true } }),
      );
      await page.reload();
      await expect(panel).toContainText(
        "Some OIDC sessions could not be signed out",
      );
      await panel.getByRole("button", { name: "Retry sign-out" }).click();
      await expect(panel).toContainText("Refresh and try again");
      await expect(panel).toContainText(
        "Some OIDC sessions could not be signed out",
      );
      await page.unroute(`**${path}`);
      await restartJellyfin(request, path, headers);
      expect((await status()).Request.Email).toBe(`${name}@example.test`);
      await page.reload();
      await expect(panel).toContainText(
        "Waiting for Identity Provider sign-in",
      );
      await page.screenshot({
        path: test.info().outputPath("admin-desktop.png"),
        fullPage: true,
      });
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({
        path: test.info().outputPath("admin-mobile.png"),
        fullPage: true,
      });
      await page.setViewportSize({ width: 1280, height: 720 });
      for (let index = 0; index < 2; index++) {
        const context = await browser.newContext({ ignoreHTTPSErrors: true });
        contexts.push(context);
        const signedIn = await context.newPage();
        await signIn(signedIn, name);
        expect((await currentUser(signedIn)).Id).toBe(target.Id);
        oidcTokens.push(await signedIn.evaluate(() => ApiClient.accessToken()));
      }
      expect((await status()).Linked).toBe(true);
      expect((await status()).Request).toBeFalsy();
      expect(
        (
          await request.delete(
            `${path}/link?version=${staleApproval.Version}`,
            { headers },
          )
        ).status(),
      ).toBe(409);
      await setConfigurationValue(
        page,
        "PasswordLoginMode",
        "DisableForLinkedUsersOnly",
      );
      expect(
        (await authenticatePassword(request, name, "local-password")).ok(),
      ).toBe(false);
      await restartJellyfin(request, path, headers);
      await setConfigurationValue(page, "Enabled", false);
      const oldIssuer = await setConfigurationValue(
        page,
        "IssuerUrl",
        "https://reconfigured.example.test",
      );
      await page.reload();
      await expect(panel).toContainText("Linked as");
      await expect(panel).toContainText("OIDC is unavailable");
      const anonymous = await browser.newContext({ ignoreHTTPSErrors: true });
      contexts.push(anonymous);
      const login = await anonymous.newPage();
      await showLogin(login);
      await expect(
        login.getByRole("button", { name: "Sign In with SSO" }),
      ).toHaveCount(0);
      await panel.getByRole("button", { name: "Unlink account" }).click();
      await expect(page.getByRole("dialog")).toContainText(
        "older untracked sessions stay active",
      );
      await page
        .getByRole("dialog")
        .getByRole("button", { name: "Unlink account", exact: true })
        .click();
      await expect(panel).toContainText("Not linked");
      await expect(panel).toContainText(
        "Automatic relinking is disabled after unlinking.",
      );
      await expect(
        page.getByRole("tab", { name: "OIDC", exact: true }),
      ).toBeFocused();
      for (const token of oidcTokens)
        expect(await tokenStatus(token)).toBe(401);
      for (const token of [rootToken, local.AccessToken, qcToken])
        expect(await tokenStatus(token)).toBe(200);
      expect(
        (await authenticatePassword(request, name, "local-password")).ok(),
      ).toBe(true);
      await setConfigurationValue(page, "IssuerUrl", oldIssuer);
      await setConfigurationValue(page, "Enabled", true);
      const deniedContext = await browser.newContext({
        ignoreHTTPSErrors: true,
      });
      contexts.push(deniedContext);
      await signInDenied(await deniedContext.newPage(), name);
      for (const criteria of [{ Subject: subject }, { Username: name }]) {
        await page.reload();
        await panel
          .getByRole("button", { name: "Link on next sign-in" })
          .click();
        const [kind, value] = Object.entries(criteria)[0];
        await panel
          .getByRole("combobox", { name: "Match by" })
          .selectOption(kind);
        await panel
          .getByRole("textbox", { name: "Value", exact: true })
          .fill(value);
        await panel.getByRole("button", { name: "Approve link" }).click();
        await expect(panel).toContainText(
          "Waiting for Identity Provider sign-in",
        );
        const context = await browser.newContext({ ignoreHTTPSErrors: true });
        contexts.push(context);
        const signedIn = await context.newPage();
        await signIn(signedIn, name);
        expect((await currentUser(signedIn)).Id).toBe(target.Id);
        const state = await status();
        expect(state.AutomaticReattachmentBlocked).toBe(false);
        expect(
          (
            await request.delete(`${path}/link?version=${state.Version}`, {
              headers,
            })
          ).ok(),
        ).toBe(true);
      }
      await restartJellyfin(request, path, headers);
      expect((await status()).AutomaticReattachmentBlocked).toBe(true);
      const before = await status();
      expect(
        (
          await request.put(`${path}/link-request`, {
            headers,
            data: { Version: before.Version, Subject: subject },
          })
        ).ok(),
      ).toBe(true);
      const staleContext = await browser.newContext({
        ignoreHTTPSErrors: true,
      });
      contexts.push(staleContext);
      const stale = await staleContext.newPage();
      await showLogin(stale);
      await stale.getByRole("button", { name: "Sign In with SSO" }).click();
      await expect(stale.locator("#username")).toBeVisible();
      const pending = await status();
      expect(
        (
          await request.delete(
            `${path}/link-request?version=${pending.Version}`,
            { headers },
          )
        ).ok(),
      ).toBe(true);
      await stale.locator("#username").fill(name);
      await stale.locator("#password").fill("password");
      await stale.locator("#kc-login").click();
      await expect(stale).toHaveURL(/oidcError=1/);
      expect((await status()).Linked).toBe(false);
      await page.goto(
        `/web/index.html#/dashboard/users/${local.User.Id}/profile`,
      );
      await expect(
        page.getByRole("tab", { name: "OIDC", exact: true }),
      ).toHaveCount(1);
      await page.goto(
        `/web/index.html#/dashboard/users/${(await currentUser(page)).Id}/profile?oidc=1`,
      );
      await expect(page.getByRole("tabpanel", { name: "OIDC" })).toContainText(
        "Not linked",
      );
    } finally {
      for (const context of contexts) await context.close();
      await status();
      await setConfigurationValue(page, "Enabled", true);
      await setConfigurationValue(
        page,
        "IssuerUrl",
        "https://oidc.localhost:8443/keycloak/realms/jellyfin",
      );
      await setConfigurationValue(page, "PasswordLoginMode", originalMode);
      const cleanup = await status();
      if (cleanup.Linked)
        expect(
          (
            await request.delete(`${path}/link?version=${cleanup.Version}`, {
              headers,
            })
          ).ok(),
        ).toBe(true);
      else if (cleanup.Request)
        expect(
          (
            await request.delete(
              `${path}/link-request?version=${cleanup.Version}`,
              { headers },
            )
          ).ok(),
        ).toBe(true);
      await request.delete(`/Users/${target.Id}`, { headers });
      await request.delete(providerLocation, { headers: providerHeaders });
    }
  });
}

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

for (const layout of ["modern", "desktop-legacy"]) {
  test(`allows sole-administrator self-unlink with local-session preservation in ${layout}`, async ({
    browser,
    page,
    request,
  }) => {
    test.setTimeout(180_000);
    await localSignIn(page, "root", "");
    const rootHeaders = {
      Authorization: `MediaBrowser Token="${await page.evaluate(() => ApiClient.accessToken())}"`,
    };
    const originalMode = await setConfigurationValue(
      page,
      "PasswordLoginMode",
      "AllowForAllUsers",
    );
    const originalRedirect = await setConfigurationValue(
      page,
      "RedirectSignInPageToProvider",
      false,
    );
    const name = `self-unlink-${layout}-${Date.now()}`;
    const created = await request.post("/Users/New", {
      headers: rootHeaders,
      data: { Name: name, Password: "local-password" },
    });
    expect(created.ok()).toBe(true);
    const target = await created.json();
    const policy = { ...target.Policy, IsAdministrator: true };
    expect(
      (
        await request.post(`/Users/${target.Id}/Policy`, {
          headers: rootHeaders,
          data: policy,
        })
      ).ok(),
    ).toBe(true);
    const providerHeaders = await providerAdmin(request);
    const providerCreated = await request.post(
      "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users",
      {
        headers: providerHeaders,
        data: {
          username: name,
          email: `${name}@example.test`,
          emailVerified: true,
          firstName: "Self",
          lastName: "Unlink",
          enabled: true,
          groups: ["/jellyfin-admins"],
          credentials: [
            { type: "password", value: "password", temporary: false },
          ],
        },
      },
    );
    expect(providerCreated.ok()).toBe(true);
    const providerLocation = providerCreated.headers().location;
    const localContext = await browser.newContext({ ignoreHTTPSErrors: true });
    const localPage = await localContext.newPage();
    await localSignIn(localPage, name, "local-password", layout);
    const localToken = await localPage.evaluate(() => ApiClient.accessToken());
    const headers = { Authorization: `MediaBrowser Token="${localToken}"` };
    const others = (
      await (await request.get("/Users", { headers })).json()
    ).filter((user) => user.Policy.IsAdministrator && user.Id !== target.Id);
    const path = `/oidc/admin/users/${target.Id}`;
    const editor = `/web/index.html#/dashboard/users/${target.Id}/profile?oidc=1`;
    const status = async () => (await request.get(path, { headers })).json();
    const contexts = [];
    try {
      for (const user of others)
        expect(
          (
            await request.post(`/Users/${user.Id}/Policy`, {
              headers,
              data: { ...user.Policy, IsAdministrator: false },
            })
          ).ok(),
        ).toBe(true);
      for (const mode of [
        "AllowForAllUsers",
        "DisableForLinkedUsersOnly",
        "DisableForAllUsers",
      ]) {
        await setConfigurationValue(localPage, "PasswordLoginMode", mode);
        await setConfigurationValue(
          localPage,
          "RedirectSignInPageToProvider",
          mode === "DisableForAllUsers",
        );
        const state = await status();
        expect(
          (
            await request.put(`${path}/link-request`, {
              headers,
              data: { Version: state.Version, Email: `${name}@example.test` },
            })
          ).ok(),
        ).toBe(true);
        const context = await browser.newContext({ ignoreHTTPSErrors: true });
        contexts.push(context);
        const oidc = await context.newPage();
        if (mode === "DisableForAllUsers") {
          await oidc.goto("/oidc/start?returnUrl=/");
          await oidc.locator("#username").fill(name);
          await oidc.locator("#password").fill("password");
          await oidc.locator("#kc-login").click();
          await expect(
            oidc.getByRole("button", { name: "User Menu" }),
          ).toBeVisible({ timeout: 30_000 });
        } else await signIn(oidc, name);
        const oidcToken = await oidc.evaluate(() => ApiClient.accessToken());
        await oidc.evaluate(
          (layout) => localStorage.setItem("layout", layout),
          layout,
        );
        await oidc.goto(editor);
        const panel = oidc.getByRole("tabpanel", { name: "OIDC" });
        await panel.getByRole("button", { name: "Unlink account" }).click();
        await expect(oidc.getByRole("dialog")).toContainText(
          "This is the only administrator",
        );
        await expect(oidc.getByRole("dialog")).toContainText(
          "If you signed in through OIDC, this session will end",
        );
        await oidc
          .getByRole("dialog")
          .getByRole("button", { name: "Unlink account", exact: true })
          .click();
        await expect(oidc).toHaveURL(/oidcSignedOut=1.*login/, {
          timeout: 30_000,
        });
        await expect(
          oidc.getByRole("button", { name: "Sign In with SSO" }),
        ).toBeVisible();
        expect(
          (
            await request.get("/Users/Me", {
              headers: { Authorization: `MediaBrowser Token="${oidcToken}"` },
            })
          ).status(),
        ).toBe(401);
        expect((await request.get("/Users/Me", { headers })).ok()).toBe(true);
        expect((await status()).Linked).toBe(false);
        expect(
          (await authenticatePassword(request, name, "local-password")).ok(),
        ).toBe(mode !== "DisableForAllUsers");
        await localPage.goto("/web/index.html#/userprofile");
        await localPage.reload();
        if (mode === "DisableForAllUsers")
          await expect(localPage.locator(".updatePasswordForm")).toBeHidden();
        else
          await expect(localPage.locator(".updatePasswordForm")).toBeVisible();
      }
      await setConfigurationValue(
        localPage,
        "PasswordLoginMode",
        "AllowForAllUsers",
      );
      await setConfigurationValue(
        localPage,
        "RedirectSignInPageToProvider",
        false,
      );
      const state = await status();
      expect(
        (
          await request.put(`${path}/link-request`, {
            headers,
            data: { Version: state.Version, Email: `${name}@example.test` },
          })
        ).ok(),
      ).toBe(true);
      const context = await browser.newContext({ ignoreHTTPSErrors: true });
      contexts.push(context);
      await signIn(await context.newPage(), name);
      await localPage.goto(editor);
      await localPage.getByRole("button", { name: "Unlink account" }).click();
      await localPage
        .getByRole("dialog")
        .getByRole("button", { name: "Unlink account", exact: true })
        .click();
      await expect(
        localPage.getByRole("tabpanel", { name: "OIDC" }),
      ).toContainText("Not linked");
      expect((await request.get("/Users/Me", { headers })).ok()).toBe(true);
    } finally {
      for (const user of others)
        expect(
          (
            await request.post(`/Users/${user.Id}/Policy`, {
              headers,
              data: user.Policy,
            })
          ).ok(),
        ).toBe(true);
      await setConfigurationValue(localPage, "PasswordLoginMode", originalMode);
      await setConfigurationValue(
        localPage,
        "RedirectSignInPageToProvider",
        originalRedirect,
      );
      const cleanup = await status();
      if (cleanup.Linked)
        expect(
          (
            await request.delete(`${path}/link?version=${cleanup.Version}`, {
              headers,
            })
          ).ok(),
        ).toBe(true);
      for (const context of contexts) await context.close();
      await localContext.close();
      await request.delete(`/Users/${target.Id}`, { headers: rootHeaders });
      await request.delete(providerLocation, { headers: providerHeaders });
    }
  });
}

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
    expect(updatedUser.Name).toBe(originalUser.Name);
    const retainedEmail = await updatedPage.evaluate(async (userId) => {
      const configuration = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      return configuration.IdentityLinks.find((link) => link.UserId === userId)
        .Email;
    }, originalUser.Id);
    expect(retainedEmail).toBe(changedEmail);
    await updatedPage.goto("/web/index.html#/userprofile");
    await expect(
      updatedPage.locator("[data-oidc-profile] [data-oidc-status-text]"),
    ).toHaveText("Linked as " + changedEmail);
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

test("preserves server-owned Identity Links across browser configuration saves", async ({
  page,
}) => {
  await signIn(page);
  const user = await currentUser(page);
  const originalLinks = await page.evaluate(async () => {
    const config = await ApiClient.getPluginConfiguration(
      "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
    );
    return config.IdentityLinks;
  });
  const otherLinks = originalLinks.map((link) =>
    link.UserId === user.Id
      ? { ...link, Subject: "another-identity-subject" }
      : link,
  );
  expect(
    otherLinks.some((link) => link.Subject === "another-identity-subject"),
  ).toBe(true);

  try {
    await setConfigurationValue(page, "IdentityLinks", otherLinks);
    const links = await page.evaluate(async () => {
      const config = await ApiClient.getPluginConfiguration(
        "4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4",
      );
      return config.IdentityLinks;
    });
    expect(links).toEqual(originalLinks);
  } finally {
    await setConfigurationValue(page, "IdentityLinks", originalLinks);
  }
});

test("keeps the linked Jellyfin User when verified email matches another username", async ({
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
  const originalContext = await browser.newContext({ ignoreHTTPSErrors: true });
  const originalPage = await originalContext.newPage();
  await signIn(originalPage);
  const originalUser = await currentUser(originalPage);
  await originalContext.close();

  try {
    user.email = email;
    expect((await updateProviderUser(request, headers, user)).ok()).toBe(true);
    const returningContext = await browser.newContext({
      ignoreHTTPSErrors: true,
    });
    const returningPage = await returningContext.newPage();
    await signIn(returningPage);
    expect((await currentUser(returningPage)).Id).toBe(originalUser.Id);
    expect((await currentUser(returningPage)).Name).toBe(originalUser.Name);
    await returningContext.close();
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

for (const layout of ["modern", "desktop-legacy"]) {
  test(`links an email-free identity from the ${layout} profile without changing the user`, async ({
    browser,
    page,
    request,
  }) => {
    await signIn(page);
    const originalMode = await setConfigurationValue(
      page,
      "PasswordLoginMode",
      "DisableForLinkedUsersOnly",
    );
    const originalEmailRequirement = await setConfigurationValue(
      page,
      "RequireVerifiedEmail",
      true,
    );
    const root = await (await authenticatePassword(request, "root", "")).json();
    const rootHeaders = {
      Authorization: `MediaBrowser Token="${root.AccessToken}"`,
    };
    const name = `local-link-${layout}`;
    const created = await request.post("/Users/New", {
      headers: rootHeaders,
      data: { Name: name, Password: "local-password" },
    });
    expect(created.ok()).toBe(true);
    const localUser = await created.json();
    const providerHeaders = await providerAdmin(request);
    const profileUrl =
      "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users/profile";
    const originalProfile = await (
      await request.get(profileUrl, { headers: providerHeaders })
    ).json();
    const optionalEmailProfile = structuredClone(originalProfile);
    delete optionalEmailProfile.attributes.find(
      (attribute) => attribute.name === "email",
    ).required;
    expect(
      (
        await request.put(profileUrl, {
          headers: providerHeaders,
          data: optionalEmailProfile,
        })
      ).ok(),
    ).toBe(true);
    const providerName = `link-${layout}`;
    const providerCreated = await request.post(
      "https://oidc.localhost:8443/keycloak/admin/realms/jellyfin/users",
      {
        headers: providerHeaders,
        data: {
          username: providerName,
          firstName: "Link",
          lastName: "Test",
          enabled: true,
          groups: ["/jellyfin-users"],
          credentials: [
            { type: "password", value: "password", temporary: false },
          ],
        },
      },
    );
    expect(providerCreated.ok()).toBe(true);
    const providerLocation = providerCreated.headers().location;
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const profile = await context.newPage();
    try {
      await profile.goto("/web/index.html#!/login");
      await profile.evaluate(
        (layout) => localStorage.setItem("layout", layout),
        layout,
      );
      await profile.reload();
      await profile
        .getByRole("textbox", { name: "User", exact: true })
        .fill(name);
      await profile
        .getByRole("textbox", { name: "Password", exact: true })
        .fill("local-password");
      await profile
        .getByRole("button", { name: "Sign In", exact: true })
        .click();
      await profile.waitForFunction(
        (userId) => window.ApiClient?.getCurrentUserId() === userId,
        localUser.Id,
      );
      await expect(profile).toHaveURL(/#!?\/home/);
      const localToken = await profile.evaluate(() => ApiClient.accessToken());
      await profile.route("**/oidc/link", (route) =>
        route.fulfill({ status: 503 }),
      );
      await profile.goto("/web/index.html#/userprofile");
      const section = profile.locator("[data-oidc-profile]");
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Unable to check link status.",
      );
      await expect(
        section.getByRole("button", { name: "Link account" }),
      ).toHaveCount(0);
      await profile.unroute("**/oidc/link");
      await section.getByRole("button", { name: "Retry" }).click();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Not linked",
      );
      await expect(section).toContainText(
        "Linking will disable password sign-in",
      );
      await expect(
        section.getByRole("button", { name: "Link account" }),
      ).toHaveClass(/emby-button/);
      await expect(
        section
          .getByRole("button", { name: "Link account" })
          .locator(".material-icons"),
      ).toHaveText("link");
      await expect(profile.locator(".updatePasswordForm")).toBeVisible();
      await setPasswordLoginMode(page, "DisableForAllUsers");
      await profile.reload();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Not linked",
      );
      await expect(profile.locator(".updatePasswordForm")).toBeHidden();
      await expect(
        profile.getByRole("button", { name: "Save Password" }),
      ).toHaveCount(0);
      await setPasswordLoginMode(page, "DisableForLinkedUsersOnly");
      await profile.reload();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Not linked",
      );
      await expect(profile.locator(".updatePasswordForm")).toBeVisible();
      await profile.screenshot({
        path: test.info().outputPath("profile-desktop.png"),
        fullPage: true,
      });
      await profile.setViewportSize({ width: 375, height: 812 });
      await profile.screenshot({
        path: test.info().outputPath("profile-mobile.png"),
        fullPage: true,
      });
      await profile.setViewportSize({ width: 1280, height: 720 });
      await profile.getByRole("button", { name: "Link account" }).click();
      await expect(profile).toHaveURL(/oidc\.localhost/);
      await profile.goBack();
      await expect(
        profile.getByRole("button", { name: "Link account" }),
      ).toBeEnabled();
      await profile.getByRole("button", { name: "Link account" }).click();
      await profile.locator("#username").fill("user");
      await profile.locator("#password").fill("password");
      await profile.locator("#kc-login").click();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Not linked",
        {
          timeout: 30_000,
        },
      );
      await expect(profile.getByRole("alert")).toHaveText(
        "We couldn't link your Identity Provider. Please try again.",
      );
      expect((await currentUser(profile)).Id).toBe(localUser.Id);
      await profile.getByRole("button", { name: "Link account" }).click();
      await expect(profile.locator("#password")).toBeVisible();
      if (await profile.locator("#reset-login").isVisible()) {
        await profile.locator("#reset-login").click();
      }
      await profile.locator("#username").fill(providerName);
      await profile.locator("#password").fill("password");
      let completionTicket;
      profile.on("request", (request) => {
        if (request.url().endsWith("/oidc/link/complete"))
          completionTicket = request.postDataJSON().Ticket;
      });
      await profile.locator("#kc-login").click();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Not linked",
        {
          timeout: 30_000,
        },
      );
      await expect(profile.getByRole("alert")).toHaveText(
        "We couldn't link your Identity Provider. Please try again.",
      );
      await setConfigurationValue(page, "RequireVerifiedEmail", false);
      await profile.getByRole("button", { name: "Link account" }).click();
      await expect(profile.locator("#password")).toBeVisible();
      if (await profile.locator("#reset-login").isVisible())
        await profile.locator("#reset-login").click();
      await profile.locator("#username").fill(providerName);
      await profile.locator("#password").fill("password");
      await profile.locator("#kc-login").click();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Linked as " + providerName,
        {
          timeout: 30_000,
        },
      );
      await expect(section.locator("button")).toHaveCount(1);
      await expect(
        section.getByRole("button", { name: "Unlink account" }),
      ).toBeVisible();
      await expect(profile.locator(".updatePasswordForm")).toBeHidden();
      await expect(
        profile.getByRole("button", { name: "Save Password" }),
      ).toHaveCount(0);
      await expect(
        profile.getByRole("button", { name: "Reset Password" }),
      ).toHaveCount(0);
      const linkedUser = await currentUser(profile);
      expect(linkedUser.Id).toBe(localUser.Id);
      expect(linkedUser.Name).toBe(name);
      expect(linkedUser.Policy.IsAdministrator).toBe(false);
      expect(await profile.evaluate(() => ApiClient.accessToken())).toBe(
        localToken,
      );
      expect(
        (await authenticatePassword(request, name, "local-password")).ok(),
      ).toBe(false);
      expect(completionTicket).toBeTruthy();
      const replay = await request.post("/oidc/link/complete", {
        headers: {
          Authorization: `MediaBrowser Token="${localToken}"`,
        },
        data: { Ticket: completionTicket },
      });
      expect(replay.status()).toBe(409);
      await setPasswordLoginMode(page, "AllowForAllUsers");
      await profile.reload();
      await expect(section.locator("[data-oidc-status-text]")).toHaveText(
        "Linked as " + providerName,
      );
      await expect(profile.locator(".updatePasswordForm")).toBeVisible();
      await expect(
        profile.getByRole("button", { name: "Save Password" }),
      ).toBeVisible();
      await setPasswordLoginMode(page, "DisableForLinkedUsersOnly");
      await profile.reload();
      await expect(profile.locator(".updatePasswordForm")).toBeHidden();
      for (let visit = 0; visit < 2; visit += 1) {
        await profile.goto("/web/index.html#/mypreferencesmenu");
        await profile.goto("/web/index.html#/userprofile");
        await expect(section).toHaveCount(1);
        await expect(section.locator("[data-oidc-status-text]")).toHaveText(
          "Linked as " + providerName,
        );
      }
      await profile.setViewportSize({ width: 375, height: 812 });
      await expect(section).toBeVisible();
      expect(
        await profile.evaluate(() => {
          const section = document.querySelector("[data-oidc-profile]");
          return (
            section.getBoundingClientRect().right <= innerWidth &&
            section.scrollWidth <= section.clientWidth
          );
        }),
      ).toBe(true);
      await profile.goto(`/web/index.html#/userprofile?userId=${root.User.Id}`);
      await expect(section).toHaveCount(0);
      await setConfigurationValue(page, "RequireVerifiedEmail", true);
      const denied = await browser.newContext({ ignoreHTTPSErrors: true });
      try {
        await signInDenied(await denied.newPage(), providerName);
      } finally {
        await denied.close();
      }
      await setConfigurationValue(page, "RequireVerifiedEmail", false);
      const returning = await browser.newContext({ ignoreHTTPSErrors: true });
      try {
        const returningPage = await returning.newPage();
        await signIn(returningPage, providerName);
        expect((await currentUser(returningPage)).Id).toBe(localUser.Id);
        expect((await currentUser(returningPage)).Name).toBe(name);
        const oidcToken = await returningPage.evaluate(() =>
          ApiClient.accessToken(),
        );
        await profile.goto("/web/index.html#/userprofile");
        await section.getByRole("button", { name: "Unlink account" }).click();
        await expect(profile.getByRole("dialog")).toHaveClass(/formDialog/);
        await expect(
          profile
            .getByRole("dialog")
            .getByRole("button", { name: "Unlink account" }),
        ).toHaveClass(/button-delete/);
        await profile
          .getByRole("dialog")
          .getByRole("button", { name: "Cancel", exact: true })
          .click();
        await expect(section.locator("[data-oidc-status-text]")).toHaveText(
          "Linked as " + providerName,
        );
        await section.getByRole("button", { name: "Unlink account" }).click();
        await profile
          .getByRole("dialog")
          .getByRole("button", { name: "Unlink account", exact: true })
          .click();
        await expect(section.locator("[data-oidc-status-text]")).toHaveText(
          "Not linked",
        );
        await expect(section).toContainText(
          "Automatic relinking is disabled after unlinking.",
        );
        await expect(profile.locator(".updatePasswordForm")).toBeVisible();
        expect(await profile.evaluate(() => ApiClient.accessToken())).toBe(
          localToken,
        );
        expect(
          (
            await request.get("/Users/Me", {
              headers: { Authorization: `MediaBrowser Token="${oidcToken}"` },
            })
          ).status(),
        ).toBe(401);
        expect(
          (
            await request.get("/Users/Me", {
              headers: { Authorization: `MediaBrowser Token="${localToken}"` },
            })
          ).ok(),
        ).toBe(true);
        await section.getByRole("button", { name: "Link account" }).click();
        await expect(profile.locator("#password")).toBeVisible();
        if (await profile.locator("#reset-login").isVisible())
          await profile.locator("#reset-login").click();
        await profile.locator("#username").fill(providerName);
        await profile.locator("#password").fill("password");
        await profile.locator("#kc-login").click();
        await expect(section.locator("[data-oidc-status-text]")).toHaveText(
          "Linked as " + providerName,
          { timeout: 30_000 },
        );
        await signIn(returningPage, providerName);
        await returningPage.evaluate(
          (layout) => localStorage.setItem("layout", layout),
          layout,
        );
        await returningPage.goto("/web/index.html#/userprofile");
        await returningPage
          .locator("[data-oidc-profile]")
          .getByRole("button", { name: "Unlink account" })
          .click();
        await returningPage
          .getByRole("dialog")
          .getByRole("button", { name: "Unlink account", exact: true })
          .click();
        await expect(returningPage).toHaveURL(/oidcSignedOut=1.*login/);
        expect(
          (
            await request.get("/Users/Me", {
              headers: { Authorization: `MediaBrowser Token="${localToken}"` },
            })
          ).ok(),
        ).toBe(true);
      } finally {
        await returning.close();
      }
    } finally {
      await context.close();
      const state = await (
        await request.get(`/oidc/admin/users/${localUser.Id}`, {
          headers: rootHeaders,
        })
      ).json();
      expect(
        (
          await request.delete(
            `/oidc/admin/users/${localUser.Id}/link?version=${state.Version}`,
            { headers: rootHeaders },
          )
        ).ok(),
      ).toBe(true);
      await setConfigurationValue(page, "PasswordLoginMode", originalMode);
      await setConfigurationValue(
        page,
        "RequireVerifiedEmail",
        originalEmailRequirement,
      );
      expect(
        (
          await request.delete(`/Users/${localUser.Id}`, {
            headers: rootHeaders,
          })
        ).ok(),
      ).toBe(true);
      expect(
        (
          await request.delete(providerLocation, { headers: providerHeaders })
        ).ok(),
      ).toBe(true);
      expect(
        (
          await request.put(profileUrl, {
            headers: providerHeaders,
            data: originalProfile,
          })
        ).ok(),
      ).toBe(true);
    }
  });
}

test("link endpoints reject anonymous requests and invalid return URLs", async ({
  request,
}) => {
  expect((await request.get("/oidc/link")).status()).toBe(401);
  expect((await request.delete("/oidc/link")).status()).toBe(401);
  expect(
    (
      await request.post("/oidc/link/retry-sign-out", {
        data: { Operation: "00000000-0000-0000-0000-000000000000" },
      })
    ).status(),
  ).toBe(401);
  expect(
    (await request.post("/oidc/link?returnUrl=/userprofile")).status(),
  ).toBe(401);
  expect(
    (
      await request.post("/oidc/link/complete", { data: { Ticket: "missing" } })
    ).status(),
  ).toBe(401);
  const root = await (await authenticatePassword(request, "root", "")).json();
  for (const returnUrl of [
    "https://attacker.test",
    "//attacker.test",
    "/\\attacker.test",
  ]) {
    const response = await request.post("/oidc/link", {
      headers: { Authorization: `MediaBrowser Token="${root.AccessToken}"` },
      params: { returnUrl },
    });
    expect(response.status()).toBe(400);
  }
  const response = await request.get("/oidc/link", {
    headers: { Authorization: `MediaBrowser Token="${root.AccessToken}"` },
  });
  expect(response.ok()).toBe(true);
  expect(response.headers()["cache-control"]).toContain("no-store");
  expect(Object.keys(await response.json()).sort()).toEqual([
    "AutomaticReattachmentBlocked",
    "CanLink",
    "CanUnlink",
    "Linked",
    "Name",
    "PartialRevocation",
    "PasswordLoginDisabled",
    "PasswordLoginMode",
    "SoleAdministrator",
    "UnlinkOperation",
    "Version",
  ]);
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
