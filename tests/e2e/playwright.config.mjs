import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './specs',
    retries: process.env.CI ? 1 : 0,
    outputDir: 'test-results',
    use: {
        baseURL: 'https://localhost:8443',
        headless: process.env.PLAYWRIGHT_HEADED !== '1',
        ignoreHTTPSErrors: true,
        screenshot: 'only-on-failure',
        trace: 'on-first-retry',
        video: 'on-first-retry',
    },
});
