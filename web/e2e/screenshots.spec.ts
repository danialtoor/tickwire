import { expect, test } from '@playwright/test'
import { openTrader } from './helpers'

// Captures the README images: npx playwright test --grep @screenshots
test.use({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 })

test('@screenshots README images', async ({ page }) => {
  test.setTimeout(120_000)
  await openTrader(page)
  await page.getByTestId('ticket-submit').click()
  await page.getByRole('button', { name: /Chaos/ }).click()
  await page.getByTestId('chaos-drop-venue').click()
  await page.getByTestId('ticket-submit').click()
  await page.waitForTimeout(800)
  await page.getByTestId('ticket-submit').click()
  await expect(page.getByTestId('inspector').locator('[data-testid=wire-row][data-disposition="Resent"]').first()).toBeVisible()
  await page.waitForTimeout(1500)
  await page.screenshot({ path: '../docs/images/trader.png' })
  await page.getByTestId('inspector').screenshot({ path: '../docs/images/inspector-recovery.png' })

  await page.goto('/analyzer')
  await page.getByRole('button', { name: 'broken-state-machine' }).click()
  await expect(page.getByText('STATE_REGRESSION', { exact: true })).toBeVisible()
  await page.screenshot({ path: '../docs/images/analyzer.png' })

  await page.goto('/ops')
  await expect(page.getByText('Ops dashboard')).toBeVisible()
  await page.waitForTimeout(2500)
  await page.screenshot({ path: '../docs/images/ops.png' })
})
