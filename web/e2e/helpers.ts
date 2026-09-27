import { expect, type Page } from '@playwright/test'

/** Fails the test if the page logs errors (ignoring the expected ones when the API is deliberately blocked). */
export function watchConsole(page: Page, allow: RegExp[] = []): () => void {
  const errors: string[] = []
  page.on('console', (m) => {
    if (m.type() === 'error' && !allow.some((r) => r.test(m.text()))) errors.push(m.text())
  })
  page.on('pageerror', (e) => errors.push(e.message))
  return () => expect(errors, errors.join('\n')).toEqual([])
}

export async function openTrader(page: Page): Promise<void> {
  await page.goto('/trade')
  await expect(page.getByText('FIX session')).toBeVisible()
  // The guest session is live once the venue side reports Active.
  await expect(page.locator('span', { hasText: /^Active$/ }).first()).toBeVisible({ timeout: 30_000 })
  await expect(page.getByTestId('ticket-submit')).toBeEnabled({ timeout: 30_000 })
}
