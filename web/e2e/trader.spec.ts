import { expect, test } from '@playwright/test'
import { openTrader, watchConsole } from './helpers'

test('@local @prod an order goes out as FIX and comes back as ExecutionReports', async ({ page }) => {
  const noErrors = watchConsole(page)
  await openTrader(page)

  await page.getByTestId('ticket-submit').click()

  await expect(page.getByTestId('blotter').locator('tbody tr')).toHaveCount(1)
  const inspector = page.getByTestId('inspector')
  await expect(inspector.locator('[data-testid=wire-row][data-msgtype="D"]').first()).toBeVisible()
  await expect(inspector.locator('[data-testid=wire-row][data-msgtype="8"]').first()).toBeVisible()
  await expect(inspector.getByText('ExecutionReport').first()).toBeVisible()
  noErrors()
})

test('@local @prod chaos: dropped messages are recovered with ResendRequest and GapFill', async ({ page }) => {
  const noErrors = watchConsole(page)
  await openTrader(page)

  await page.getByRole('button', { name: /Chaos/ }).click()
  await page.getByTestId('chaos-drop-venue').click()
  await expect(page.getByText(/will drop its next/)).toBeVisible()
  // Send two orders so there's always a message after the dropped ones to expose the gap.
  await page.getByTestId('ticket-submit').click()
  await page.waitForTimeout(500)
  await page.getByTestId('ticket-submit').click()

  const inspector = page.getByTestId('inspector')
  await expect(inspector.locator('[data-testid=wire-row][data-msgtype="2"]').first()).toBeVisible()
  await expect(inspector.locator('[data-testid=wire-row][data-disposition="Resent"]').first()).toBeVisible()
  noErrors()
})

test('@local @prod @mobile trader renders on a phone without horizontal scroll', async ({ page }) => {
  const noErrors = watchConsole(page)
  await openTrader(page)
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(1)
  noErrors()
})
