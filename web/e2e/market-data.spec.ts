import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers'

test('@local @prod connecting the demo feed adds live quotes to the chain', async ({ page }) => {
  const noErrors = watchConsole(page)
  await page.goto('/data')
  for (const id of ['spiderrock', 'databento', 'polygon', 'tradier', 'alpaca', 'demo']) {
    await expect(page.getByTestId(`provider-${id}`)).toBeVisible()
  }

  await page.getByTestId('provider-demo').getByRole('button', { name: 'Connect' }).click()
  await expect(page.getByText('Streaming').first()).toBeVisible()

  await page.getByRole('link', { name: 'View in chain' }).click()
  await expect(page.getByRole('columnheader', { name: 'Live' }).first()).toBeVisible()
  await expect(page.getByText(/\d+\.\d\d × \d+\.\d\d/).first()).toBeVisible()
  noErrors()
})
