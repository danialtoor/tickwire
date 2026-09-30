import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers'

test('@local provisioning a trading session and a drop copy', async ({ page }) => {
  const noErrors = watchConsole(page)
  await page.goto('/connect')
  await page.getByRole('button', { name: 'Provision credentials' }).click()
  await expect(page.getByText(/^BYO-[A-Z0-9]{6}$/)).toBeVisible()

  await page.getByRole('radio', { name: 'Drop copy' }).click()
  await page.getByRole('button', { name: 'Provision credentials' }).click()
  await expect(page.getByText(/^DC-[A-Z0-9]{6}$/)).toBeVisible()
  await expect(page.getByText('CopyMsgIndicator(797)=Y')).toBeVisible()
  await expect(page.getByRole('button', { name: /BYO-.* · trading/ })).toBeVisible()
  noErrors()
})
