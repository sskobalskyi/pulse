import { expect, test, type Page } from '@playwright/test'

// Needs the stack with authentication on, so it only runs when asked to:
//   docker compose -f docker-compose.yml -f docker-compose.auth.yml up --build -d --wait
//   PULSE_AUTH_E2E=1 PULSE_WEB_URL=http://localhost:8080 npx playwright test auth
// For the shared realm (docker-compose.shared-realm.yml): add PULSE_KEYCLOAK_URL=http://localhost:8181 PULSE_REALM=greenhaus
const KEYCLOAK = process.env.PULSE_KEYCLOAK_URL ?? 'http://localhost:8180'
const REALM = process.env.PULSE_REALM ?? 'pulse'
const API = process.env.PULSE_API_URL ?? 'http://localhost:5080'
const V2 = `${API}/api/pulse/v2`

test.skip(!process.env.PULSE_AUTH_E2E, 'needs the stack from docker-compose.auth.yml')

async function token(realm: string, params: Record<string, string>, client = 'admin-cli'): Promise<string> {
  const response = await fetch(`${KEYCLOAK}/realms/${realm}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: client, ...params }),
  })
  expect(response.ok, await response.clone().text()).toBe(true)
  return ((await response.json()) as { access_token: string }).access_token
}

async function subjectOf(username: string): Promise<string> {
  const admin = await token('master', { grant_type: 'password', username: 'admin', password: 'admin' })
  const response = await fetch(`${KEYCLOAK}/admin/realms/${REALM}/users?username=${username}&exact=true`, { headers: { Authorization: `Bearer ${admin}` } })
  return ((await response.json()) as { id: string }[])[0].id
}

async function signIn(page: Page, username: string) {
  await page.goto('/')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.locator('#username').fill(username)
  await page.locator('#password').fill(username)
  await page.locator('#kc-login').click()
}

test('a user signs in and sees only their own device, one button per channel', async ({ page }) => {
  const service = await token(REALM, { grant_type: 'client_credentials', client_secret: 'pulse-dev-secret' }, 'pulse-service')
  const headers = { Authorization: `Bearer ${service}`, 'Content-Type': 'application/json' }
  const id = `e2e-${Date.now().toString(36)}`

  const call = async (path: string, body?: unknown) => {
    const response = await fetch(`${V2}${path}`, { method: 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body) })
    expect(response.ok, `${path}: ${await response.clone().text()}`).toBe(true)
  }

  // The token is the SHA-256 of "t", as the contract defines it.
  const { createHash } = await import('node:crypto')
  await call('/devices', {
    id,
    model: 'greenhaus-pot-v1',
    capabilities: { channels: [{ id: 'soil-1', metric: 'soil_moisture' }, { id: 'soil-2', metric: 'soil_moisture' }], actuators: [] },
    credentialHash: 'h',
    claimTokenHash: createHash('sha256').update('t').digest('hex'),
    claimTokenExpiresAt: new Date(Date.now() + 3_600_000).toISOString(),
  })
  await call(`/devices/${id}/ship`)
  await call(`/devices/${id}/claim`, { token: 't', ownerId: await subjectOf('alice') })

  // Keep the device reporting while the browser looks at it.
  const send = () =>
    fetch(`${API}/api/telemetry`, {
      method: 'POST',
      headers,
      body: JSON.stringify([
        { deviceId: id, metric: 'soil_moisture', channel: 'soil-1', value: 41, timestamp: new Date().toISOString() },
        { deviceId: id, metric: 'soil_moisture', channel: 'soil-2', value: 57, timestamp: new Date().toISOString() },
      ]),
    })
  await send()
  const timer = setInterval(() => void send(), 1000)

  try {
    // Nobody is signed in: there is a login and no data.
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Sign in to Pulse' })).toBeVisible()

    await signIn(page, 'alice')
    await expect(page.getByRole('status')).toContainText('Live')
    await expect(page.getByText('Signed in as alice')).toBeVisible()

    await expect(page.getByText(id)).toBeVisible()
    // Earlier runs leave their devices with alice, so look inside this run's.
    const mine = page.getByRole('listitem').filter({ hasText: id })
    const zone1 = mine.getByRole('button', { name: /soil-1 · Soil moisture/ })
    const zone2 = mine.getByRole('button', { name: /soil-2 · Soil moisture/ })
    await expect(zone1).toContainText('41.0')
    await expect(zone2).toContainText('57.0')

    await zone2.click()
    await expect(page.getByRole('heading', { name: new RegExp(`${id} · soil-2 · Soil moisture`) })).toBeVisible()
    await expect(page.locator('.chart canvas').first()).toBeVisible()

    // The session survives a reload, and signing out brings the login back.
    await page.reload()
    await expect(page.getByText(id)).toBeVisible()
    await page.getByRole('button', { name: 'Sign out' }).click()
    // The button only comes back once the identity provider has ended the session and sent the browser home.
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()

    // Another user does not see it.
    await signIn(page, 'bob')
    await expect(page.getByText('Signed in as bob')).toBeVisible()
    await expect(page.getByRole('status')).toContainText('Live')
    await expect(page.getByText(id)).toHaveCount(0)
  } finally {
    clearInterval(timer)
  }
})
