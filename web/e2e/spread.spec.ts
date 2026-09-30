import { expect, test } from '@playwright/test'
import { openTrader, watchConsole } from './helpers'

test('@local @prod a vertical spread goes out as NewOrderMultileg and fills both legs', async ({ page }) => {
  const noErrors = watchConsole(page)
  await openTrader(page)

  await page.getByTestId('ticket-tab-spread').click()
  await page.getByRole('button', { name: 'Call vertical', exact: true }).click()
  await page.getByTestId('spread-submit').click()

  const inspector = page.getByTestId('inspector')
  await expect(inspector.locator('[data-testid=wire-row][data-msgtype="AB"]').first()).toBeVisible()
  await expect(inspector.getByText(/Leg fill buy/).first()).toBeVisible()
  await expect(inspector.getByText(/Leg fill sell/).first()).toBeVisible()
  await expect(page.getByTestId('blotter').getByText(/C vertical/)).toBeVisible()
  noErrors()
})
