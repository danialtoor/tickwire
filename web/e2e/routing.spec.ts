import { expect, test } from '@playwright/test'
import { openTrader, watchConsole } from './helpers'

test('@local @prod a directed market order fills on the chosen exchange', async ({ page }) => {
  const noErrors = watchConsole(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await openTrader(page)

  const venues = page.getByTestId('venue-quotes')
  for (const code of ['TWX', 'NOVA', 'ARGO']) await expect(venues).toContainText(code)

  await page.getByRole('button', { name: 'nova', exact: true }).click()
  await page.getByRole('button', { name: 'market', exact: true }).click()
  await page.getByTestId('ticket-submit').click()

  const row = page.getByTestId('blotter').locator('tbody tr').first()
  await expect(row).toContainText('Filled')
  await expect(row).toContainText('NOVA')
  noErrors()
})
