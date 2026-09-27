import { expect, test } from '@playwright/test'
import { watchConsole } from './helpers'

test('@local @prod analyzer flags the unfilled gap in the sample log', async ({ page }) => {
  const noErrors = watchConsole(page)
  await page.goto('/analyzer')
  await page.getByRole('button', { name: 'gap-recovered-and-unfilled' }).click()

  await expect(page.getByText('SEQ_GAP', { exact: true })).toBeVisible()
  await expect(page.getByText('Sequence gap that was never filled')).toBeVisible()
  await expect(page.getByText('SEQ_GAP_RECOVERED', { exact: true })).toBeVisible()
  noErrors()
})

test('@local @prod analyzer works in the browser when the API is down', async ({ page }) => {
  await page.route('**/api/**', (route) => route.abort())
  await page.goto('/analyzer')
  await expect(page.getByTestId('replay-banner')).toBeVisible()
  await page.getByRole('button', { name: 'sequence-too-low' }).click()
  await expect(page.getByText('SEQ_TOO_LOW', { exact: true })).toBeVisible()
  await expect(page.getByText(/TypeScript engine in your browser/)).toBeVisible()
})
