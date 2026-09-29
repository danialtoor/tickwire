import { defineConfig, devices } from '@playwright/test'

// Local: starts `vite` (which proxies to the API on :8080). Production: PLAYWRIGHT_BASE_URL=https://tickwire-fix.vercel.app
const baseURL = process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173'
const local = !process.env.PLAYWRIGHT_BASE_URL

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1, // each test provisions a guest; the API allows 10 per minute per IP
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, grep: /@mobile/ },
  ],
  webServer: local
    ? { command: 'npm run dev -- --port 5173 --strictPort', url: 'http://localhost:5173', reuseExistingServer: true, timeout: 60_000 }
    : undefined,
})
