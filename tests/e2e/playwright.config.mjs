import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./specs",
  timeout: 60_000,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  outputDir: "test-results",
  use: {
    baseURL: "https://localhost:8443",
    headless: true,
    ignoreHTTPSErrors: true,
    screenshot: "only-on-failure",
    trace: "on-first-retry",
    video: "on-first-retry",
  },
});
