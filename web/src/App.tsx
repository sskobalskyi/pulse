import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import type { User } from 'oidc-client-ts'
import Dashboard from './Dashboard'
import { authEnabled, onUserChange, restoreUser, signIn, signOut } from './auth'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader } from '@/components/ui/card'

type Session = { state: 'loading' } | { state: 'signedOut'; failed: boolean } | { state: 'signedIn'; user?: User }

/** The dashboard, behind a sign-in when the API asks for one. With authentication off it is just the dashboard. */
export default function App() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  // Signing out removes the user and then leaves for the identity provider; the page waits instead of flashing the login.
  const leaving = useRef(false)
  const [session, setSession] = useState<Session>(authEnabled ? { state: 'loading' } : { state: 'signedIn' })

  useEffect(() => {
    if (!authEnabled) return

    let subject: string | undefined
    restoreUser().then(
      (user) => {
        subject = user?.profile.sub
        setSession(user ? { state: 'signedIn', user } : { state: 'signedOut', failed: false })
      },
      () => setSession({ state: 'signedOut', failed: true }),
    )
    return onUserChange((user) => {
      if (leaving.current) return
      // What was fetched for one person must not be shown to the next. A renewed token of the same person changes nothing.
      if (user?.profile.sub !== subject) queryClient.clear()
      subject = user?.profile.sub
      setSession(user ? { state: 'signedIn', user } : { state: 'signedOut', failed: false })
    })
  }, [queryClient])

  if (session.state === 'signedIn') {
    const user = session.user
    return (
      <Dashboard
        userName={user?.profile.preferred_username ?? user?.profile.name ?? user?.profile.sub}
        onSignOut={
          user
            ? () => {
                leaving.current = true
                setSession({ state: 'loading' })
                void signOut()
              }
            : undefined
        }
      />
    )
  }

  return (
    <div className="flex min-h-svh items-center justify-center p-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <h1 className="font-heading text-xl font-semibold">{t('auth.title')}</h1>
          <p className="text-sm text-muted-foreground">{t('auth.description')}</p>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {session.state === 'loading' ? (
            <p role="status" className="text-sm text-muted-foreground">
              {t('auth.checking')}
            </p>
          ) : (
            <>
              {session.failed && (
                <p role="alert" className="text-sm text-destructive">
                  {t('auth.failed')}
                </p>
              )}
              <Button onClick={() => void signIn()}>{t('auth.signIn')}</Button>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
