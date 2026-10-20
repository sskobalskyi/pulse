import { expect, test } from '@playwright/test'

test('shows live devices, a live chart and the alert feed', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('status')).toContainText('Live')

  const firstMetric = page.getByRole('button', { pressed: false }).first()
  await expect(firstMetric).toBeVisible()
  await firstMetric.click()

  await expect(page.locator('.chart canvas').first()).toBeVisible()
  await expect(page.getByRole('button', { pressed: true })).toHaveCount(1)
  await page.waitForTimeout(3000) // let the chart accumulate a few live points for the screenshot
  await page.screenshot({ path: 'e2e/screenshots/dashboard-en.png', fullPage: true })
})

test('switches the UI to Ukrainian and remembers it', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Devices' })).toBeVisible()

  await page.getByRole('combobox').selectOption('uk')
  await expect(page.getByRole('heading', { name: 'Пристрої' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Сповіщення' })).toBeVisible()

  await expect(page.getByRole('status')).toContainText('Наживо')
  await page.getByRole('button', { pressed: false }).first().click()
  await expect(page.locator('.chart canvas').first()).toBeVisible()
  await page.waitForTimeout(3000) // let the chart accumulate a few live points for the screenshot
  await page.screenshot({ path: 'e2e/screenshots/dashboard-uk.png', fullPage: true })

  await page.reload()
  await expect(page.getByRole('heading', { name: 'Пристрої' })).toBeVisible()
})
