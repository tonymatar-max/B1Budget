import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, getCompanyId, setCompanyId, type Company, type Me } from './api'
import { AuthContext, type Auth } from './auth'
import LoginPage from './pages/LoginPage'
import ApprovalsPage from './pages/ApprovalsPage'
import UsersPage from './pages/UsersPage'
import NotificationsPage from './pages/NotificationsPage'
import BudgetsPage from './pages/BudgetsPage'
import EditorPage from './pages/EditorPage'
import BvaPage from './pages/BvaPage'
import ComparePage from './pages/ComparePage'
import GroupPage from './pages/GroupPage'
import HistoryPage from './pages/HistoryPage'
import CompaniesPage from './pages/CompaniesPage'

export type Route =
  | { page: 'budgets' }
  | { page: 'editor'; id: number; brand?: string }
  | { page: 'bva'; id?: number }
  | { page: 'compare'; a?: number; b?: number }
  | { page: 'group' }
  | { page: 'history'; runId?: number }
  | { page: 'companies'; id?: number }
  | { page: 'approvals'; v?: number }
  | { page: 'users' }
  | { page: 'email' }

const num = (v: string | null) => (v ? Number(v) : undefined)

function parse(hash: string): Route {
  const [path, q] = hash.replace(/^#\/?/, '').split('?')
  const params = new URLSearchParams(q ?? '')
  const parts = path.split('/')
  if (parts[0] === 'budgets' && parts[1]) return { page: 'editor', id: Number(parts[1]), brand: params.get('b') ?? undefined }
  if (parts[0] === 'bva') return { page: 'bva', id: params.get('v') ? Number(params.get('v')) : undefined }
  if (parts[0] === 'compare') return { page: 'compare', a: num(params.get('a')), b: num(params.get('b')) }
  if (parts[0] === 'group') return { page: 'group' }
  if (parts[0] === 'history') return { page: 'history', runId: parts[1] ? Number(parts[1]) : undefined }
  if (parts[0] === 'approvals') return { page: 'approvals', v: num(params.get('v')) }
  if (parts[0] === 'users') return { page: 'users' }
  if (parts[0] === 'email') return { page: 'email' }
  if (parts[0] === 'companies' || parts[0] === 'settings') return { page: 'companies', id: parts[1] ? Number(parts[1]) : undefined }
  return { page: 'budgets' }
}

export function href(r: Route): string {
  switch (r.page) {
    case 'editor': return `#/budgets/${r.id}${r.brand ? `?b=${encodeURIComponent(r.brand)}` : ''}`
    case 'bva': return r.id ? `#/bva?v=${r.id}` : '#/bva'
    case 'compare': return `#/compare${r.a || r.b ? `?a=${r.a ?? ''}&b=${r.b ?? ''}` : ''}`
    case 'group': return '#/group'
    case 'history': return r.runId ? `#/history/${r.runId}` : '#/history'
    case 'approvals': return r.v ? `#/approvals?v=${r.v}` : '#/approvals'
    case 'users': return '#/users'
    case 'email': return '#/email'
    case 'companies': return r.id ? `#/companies/${r.id}` : '#/companies'
    default: return '#/budgets'
  }
}

export const go = (r: Route) => { window.location.hash = href(r) }

/** Pages whose URL points at one company's records — leave them when the user switches company. */
const companyBound = (r: Route) => r.page === 'editor' || r.page === 'compare' || (r.page === 'bva' && !!r.id) || (r.page === 'history' && !!r.runId)

/** Sign-in gate: first-run setup → login → forced password change → the app. */
export default function App() {
  const [state, setState] = useState<{ needsSetup: boolean; user: Me | null } | null>(null)
  const load = useCallback(() => { api.authState().then(setState).catch(() => setState({ needsSetup: false, user: null })) }, [])
  useEffect(load, [load])
  useEffect(() => {
    window.addEventListener('auth-required', load)
    return () => window.removeEventListener('auth-required', load)
  }, [load])

  if (!state) return null
  // Keyed by mode so no field (e.g. the login password) carries over into the next screen.
  if (state.needsSetup) return <LoginPage key="setup" mode="setup" onDone={load} />
  if (!state.user) return <LoginPage key="login" mode="login" onDone={load} />
  if (state.user.mustChangePassword) return <LoginPage key="change" mode="change" me={state.user} onDone={load} />
  return <Shell key={state.user.id} me={state.user} reloadMe={load} />
}

function Shell({ me, reloadMe }: { me: Me; reloadMe: () => void }) {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash))
  const [companies, setCompanies] = useState<Company[] | null>(null)
  const [companyId, setActive] = useState<number | null>(getCompanyId())

  useEffect(() => {
    const h = () => setRoute(parse(window.location.hash))
    window.addEventListener('hashchange', h)
    return () => window.removeEventListener('hashchange', h)
  }, [])

  // The active company can change from the switcher or from a page (e.g. opening another company's budget).
  useEffect(() => {
    const h = (e: Event) => setActive((e as CustomEvent<number>).detail)
    window.addEventListener('company-changed', h)
    return () => window.removeEventListener('company-changed', h)
  }, [])

  const reloadCompanies = useCallback(() => {
    api.companies().then(list => {
      setCompanies(list)
      const cur = getCompanyId()
      if (list.length && !list.some(c => c.id === cur)) setCompanyId(list[0].id)
    }).catch(() => setCompanies([]))
  }, [])
  useEffect(reloadCompanies, [reloadCompanies])

  const company = companies?.find(c => c.id === companyId) ?? null
  const auth: Auth = useMemo(() => ({
    me,
    isAdmin: me.role === 'Admin',
    isApprover: me.role !== 'User',
    refresh: reloadMe,
    logout: () => { api.logout().finally(reloadMe) },
  }), [me, reloadMe])
  const [inboxCount, setInboxCount] = useState(0)
  useEffect(() => {
    if (!auth.isApprover) return
    const tick = () => api.inbox().then(i => setInboxCount(i.length)).catch(() => {})
    tick()
    const t = setInterval(tick, 60000)
    window.addEventListener('workflow-changed', tick)
    return () => { clearInterval(t); window.removeEventListener('workflow-changed', tick) }
  }, [auth.isApprover, route])

  const switchCompany = (id: number) => {
    setCompanyId(id)
    if (companyBound(route)) go({ page: 'budgets' })
  }

  const nav = (label: string, r: Route, active: boolean, badge?: number) => (
    <button className={`nav-item${active ? ' active' : ''}`} onClick={() => go(r)}>
      {label}{badge ? <span className="status" style={{ minWidth: 0, marginLeft: 'auto', padding: '0 7px' }}>{badge}</span> : null}
    </button>
  )

  if (companies && companies.length === 0 && !auth.isAdmin)
    return (
      <div style={{ minHeight: '100%', display: 'grid', placeItems: 'center', padding: 16 }}>
        <div className="panel" style={{ padding: 28, maxWidth: 420, textAlign: 'center' }}>
          <div className="stack" style={{ alignItems: 'center' }}>
            <div className="brand-mark large"><span className="brand-node" /></div>
            <h1 style={{ fontSize: 18 }}>No department assigned yet</h1>
            <span className="muted">{me.displayName}, an administrator needs to link you to your department (cost center) before you can enter a budget.</span>
            <button onClick={auth.logout}>Sign out</button>
          </div>
        </div>
      </div>
    )

  return (
    <AuthContext.Provider value={auth}>
    <div className="shell">
      <header className="topbar">
        <div className="brand-mark"><span className="brand-node" /></div>
        <div className="product-title"><strong>Nexus</strong><span className="product-sub">B1 Budget</span></div>
        <div className="grow" />
        {companies && companies.length > 0 && (
          <div className="row" style={{ gap: 6, flexWrap: 'nowrap' }}>
            <span className="small muted">Company</span>
            <select value={companyId ?? ''} onChange={e => switchCompany(Number(e.target.value))} aria-label="Active company"
              style={{ fontWeight: 600, maxWidth: 220 }}>
              {companies.map(c => <option key={c.id} value={c.id}>{c.name}{c.currency ? ` (${c.currency})` : ''}</option>)}
            </select>
            {company && auth.isAdmin && (company.mode === 'Mock'
              ? <span className="status warn" title="Demo data — nothing is sent to SAP B1">Mock</span>
              : <span className="status" title={company.serviceLayerUrl}>{company.companyDb} · Dim {company.dimension}</span>)}
          </div>
        )}
        <div className="row" style={{ gap: 8, flexWrap: 'nowrap', marginLeft: 12, paddingLeft: 12, borderLeft: '1px solid var(--line)' }}>
          <span style={{ fontWeight: 600 }}>{me.displayName}</span>
          <span className="small muted">{me.role}</span>
          <button className="link small" onClick={auth.logout}>Sign out</button>
        </div>
      </header>
      <nav className="sidebar">
        <div className="nav-label">Plan</div>
        {nav(auth.isAdmin ? 'Budgets' : 'My budget', { page: 'budgets' }, route.page === 'budgets' || route.page === 'editor')}
        {nav(auth.isAdmin ? 'Global view' : 'Approvals', { page: 'approvals' }, route.page === 'approvals', auth.isApprover ? inboxCount : 0)}
        {nav('Compare revisions', { page: 'compare' }, route.page === 'compare')}
        {nav('Budget vs actual', { page: 'bva' }, route.page === 'bva')}
        {auth.isAdmin && (companies?.length ?? 0) > 1 && nav('Group report', { page: 'group' }, route.page === 'group')}
        {auth.isAdmin && <>
          <div className="nav-label">Administration</div>
          {nav('Push history', { page: 'history' }, route.page === 'history')}
          {nav('Users', { page: 'users' }, route.page === 'users')}
          {nav('E-mail', { page: 'email' }, route.page === 'email')}
          {nav('Companies', { page: 'companies' }, route.page === 'companies')}
        </>}
      </nav>
      {/* Keyed by company so every page reloads its data when the active company changes. */}
      <main key={companyId ?? 0}>
        {route.page === 'budgets' && <BudgetsPage />}
        {route.page === 'editor' && <EditorPage key={`${route.id}-${route.brand ?? ''}`} id={route.id} initialBrand={route.brand} settings={company} />}
        {route.page === 'bva' && <BvaPage versionId={route.id} />}
        {route.page === 'compare' && <ComparePage key={`${route.a}-${route.b}`} a={route.a} b={route.b} />}
        {route.page === 'approvals' && <ApprovalsPage versionId={route.v} />}
        {route.page === 'group' && (auth.isAdmin ? <GroupPage /> : <Denied />)}
        {route.page === 'history' && (auth.isAdmin ? <HistoryPage runId={route.runId} /> : <Denied />)}
        {route.page === 'users' && (auth.isAdmin ? <UsersPage /> : <Denied />)}
        {route.page === 'email' && (auth.isAdmin ? <NotificationsPage /> : <Denied />)}
        {route.page === 'companies' && (auth.isAdmin ? <CompaniesPage selectedId={route.id} onChanged={reloadCompanies} /> : <Denied />)}
      </main>
    </div>
    </AuthContext.Provider>
  )
}

function Denied() {
  return <div className="page"><div className="banner warn">This page is for administrators.</div></div>
}
