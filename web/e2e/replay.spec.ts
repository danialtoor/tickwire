import { expect, test } from '@playwright/test'

test('@local @prod replay mode keeps the trader alive when the backend is unreachable', async ({ page }) => {
  await page.route('**/api/**', (route) => route.abort())
  await page.route('**/hubs/**', (route) => route.abort())
  await page.goto('/trade')

  await expect(page.getByTestId('replay-banner')).toBeVisible()
  await expect(page.getByTestId('inspector').locator('[data-testid=wire-row]').first()).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('columnheader', { name: 'Strike' })).toBeVisible()
})

test('@local @prod landing page renders without errors', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))
  await page.goto('/')
  await expect(page.getByRole('link', { name: 'Launch Trader' })).toBeVisible()
  await expect(page.getByText(/Not affiliated with any trading firm/)).toBeVisible()
  expect(errors).toEqual([])
})
