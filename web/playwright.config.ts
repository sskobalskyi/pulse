import { defineConfig } from '@playwright/test'

// E2E runs against the local stack: `docker compose up -d`, the API, the simulator and `npm run dev`.
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  use: {
    baseURL: process.env.PULSE_WEB_URL ?? 'http://localhost:5173',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
})
