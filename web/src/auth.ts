import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts'

/**
 * Sign-in against the identity provider the API trusts (Keycloak in the compose override), with the authorization
 * code flow and PKCE. It is off unless an authority is configured at build time, which is how the plain stack runs:
 * the API accepts anonymous callers then and the dashboard never shows a login.
 */
const authority = import.meta.env.VITE_AUTH_AUTHORITY as string | undefined
const clientId = (import.meta.env.VITE_AUTH_CLIENT_ID as string | undefined) ?? 'pulse-web'

export const authEnabled = Boolean(authority)

const manager = authority
  ? new UserManager({
      authority,
      client_id: clientId,
      redirect_uri: `${window.location.origin}/`,
      post_logout_redirect_uri: `${window.location.origin}/`,
      response_type: 'code',
      scope: 'openid',
      automaticSilentRenew: true,
      // The tab is the session: closing it signs out, and no token is left in storage that outlives it.
      userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    })
  : null

/** Finishes a sign-in the identity provider just redirected back from, or restores the one of this tab. */
export async function restoreUser(): Promise<User | null> {
  if (!manager) return null

  const params = new URLSearchParams(window.location.search)
  if (params.has('code') && params.has('state')) {
    const user = await manager.signinCallback()
    // The code and state are single use; leaving them in the address would break a reload.
    window.history.replaceState({}, '', window.location.pathname)
    return user ?? null
  }

  const user = await manager.getUser()
  return user && !user.expired ? user : null
}

export const signIn = () => manager?.signinRedirect()

export const signOut = () => manager?.signoutRedirect()

export const onUserChange = (listener: (user: User | null) => void) => {
  if (!manager) return () => {}
  const loaded = (user: User) => listener(user)
  const gone = () => listener(null)
  manager.events.addUserLoaded(loaded)
  manager.events.addUserUnloaded(gone)
  manager.events.addSilentRenewError(gone)
  return () => {
    manager.events.removeUserLoaded(loaded)
    manager.events.removeUserUnloaded(gone)
    manager.events.removeSilentRenewError(gone)
  }
}

/** The current access token, for the Authorization header and the SignalR connection. Null when nobody is signed in. */
export async function accessToken(): Promise<string | null> {
  if (!manager) return null
  const user = await manager.getUser()
  return user && !user.expired ? user.access_token : null
}
