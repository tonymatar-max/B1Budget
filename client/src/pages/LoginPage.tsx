import { useState, type FormEvent } from 'react'
import { api, type Me } from '../api'

type Mode = 'login' | 'setup' | 'change'

/** Full-screen sign-in: first-run administrator setup, normal login, or the forced first password change. */
export default function LoginPage({ mode, me, onDone }: { mode: Mode; me?: Me | null; onDone: () => void }) {
  const [userName, setUserName] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [current, setCurrent] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (mode !== 'login' && password !== confirm) { setError('The passwords don’t match.'); return }
    setBusy(true)
    try {
      if (mode === 'setup') await api.setup({ userName, displayName, password })
      else if (mode === 'login') await api.login(userName, password)
      else await api.changePassword(current, password)
      onDone()
    } catch (err) { setError((err as Error).message) } finally { setBusy(false) }
  }

  const title = mode === 'setup' ? 'Create the administrator' : mode === 'change' ? 'Choose a new password' : 'Sign in'
  return (
    <div style={{ minHeight: '100%', display: 'grid', placeItems: 'center', padding: 16 }}>
      <form className="panel" onSubmit={submit} style={{ width: 'min(400px, 100%)', padding: '28px 28px 24px' }}>
        <div className="stack" style={{ alignItems: 'center', textAlign: 'center', marginBottom: 18 }}>
          <div className="brand-mark large"><span className="brand-node" /></div>
          <div className="product-title" style={{ justifyContent: 'center' }}><strong>Nexus</strong><span className="product-sub">B1 Budget</span></div>
          <h1 style={{ fontSize: 18 }}>{title}</h1>
          {mode === 'setup' && <span className="small muted">First start — this account manages companies, users and budgets.</span>}
          {mode === 'change' && <span className="small muted">{me?.displayName}, your password was set by an administrator. Pick your own to continue.</span>}
        </div>
        <div className="stack">
          {error && <div className="banner error">{error}</div>}
          {mode !== 'change' && (
            <label className="field"><span>User name</span>
              <input value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" autoFocus required /></label>
          )}
          {mode === 'setup' && (
            <label className="field"><span>Full name</span>
              <input value={displayName} onChange={e => setDisplayName(e.target.value)} autoComplete="name" /></label>
          )}
          {mode === 'change' && (
            <label className="field"><span>Current password</span>
              <input type="password" value={current} onChange={e => setCurrent(e.target.value)} autoComplete="current-password" autoFocus required /></label>
          )}
          <label className="field"><span>{mode === 'login' ? 'Password' : 'New password'}</span>
            <input type="password" value={password} onChange={e => setPassword(e.target.value)}
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'} required minLength={mode === 'login' ? undefined : 8} /></label>
          {mode !== 'login' && (
            <label className="field"><span>Repeat new password</span>
              <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} autoComplete="new-password" required /></label>
          )}
          {mode !== 'login' && <span className="small muted">At least 8 characters.</span>}
          <button className="primary" type="submit" disabled={busy} style={{ justifyContent: 'center', minHeight: 36 }}>
            {busy ? 'Please wait…' : mode === 'setup' ? 'Create and sign in' : mode === 'change' ? 'Save password' : 'Sign in'}
          </button>
        </div>
      </form>
    </div>
  )
}
