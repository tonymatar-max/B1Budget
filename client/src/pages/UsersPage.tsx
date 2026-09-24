import { useEffect, useMemo, useState } from 'react'
import { api, type AppUser, type Brand, type Company, type DepartmentRef, type UserForm, type UserRole } from '../api'
import { useAuth } from '../auth'
import { when } from '../format'
import { Modal, useToast } from '../ui'

const roleHelp: Record<UserRole, string> = {
  User: 'Enters the budget of their own departments and submits it to their manager.',
  Manager: 'Same as a user, plus approves or rejects what their team submits.',
  Admin: 'Finance / administrator: all companies and cost centers, users, final approval and push to SAP B1.',
}

export default function UsersPage() {
  const { me } = useAuth()
  const [users, setUsers] = useState<AppUser[] | null>(null)
  const [companies, setCompanies] = useState<Company[]>([])
  const [editing, setEditing] = useState<AppUser | 'new' | null>(null)
  const [showInactive, setShowInactive] = useState(false)
  const [, setToast, toastNode] = useToast()

  const load = () => api.users().then(setUsers).catch(e => setToast({ kind: 'error', text: e.message }))
  useEffect(() => { load(); api.companies().then(setCompanies) }, [])

  const byId = useMemo(() => new Map((users ?? []).map(u => [u.id, u])), [users])
  const companyName = (id: number) => companies.find(c => c.id === id)?.name ?? `#${id}`
  const shown = (users ?? []).filter(u => showInactive || u.active)

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Users</h1>
          <span className="muted">Who can sign in, which departments (cost centers) they budget, and who approves their submissions.</span>
        </div>
        <label className="check"><input type="checkbox" checked={showInactive} onChange={e => setShowInactive(e.target.checked)} /> Show disabled</label>
        <button className="primary" onClick={() => setEditing('new')}>Add user</button>
      </div>

      <div className="panel flush">
        <div className="panel-body table-wrap">
          {!users ? <div className="empty">Loading…</div> : (
            <table className="data">
              <thead><tr><th>Name</th><th>User name</th><th>Role</th><th>Manager (approver)</th><th>Departments</th><th>Last sign-in</th><th /></tr></thead>
              <tbody>{shown.map(u => (
                <tr key={u.id} className="clickable" onClick={() => setEditing(u)} style={u.active ? undefined : { opacity: .55 }}>
                  <td><strong>{u.displayName}</strong>{u.id === me.id && <span className="small muted"> · you</span>}
                    {!u.active && <span className="status neutral" style={{ marginLeft: 6 }}>Disabled</span>}
                    {u.mustChangePassword && u.active && <div className="small muted">must set a new password at next sign-in</div>}
                    {!u.email && u.active && <div className="small warn">no e-mail — won’t get notifications</div>}</td>
                  <td className="code">{u.userName}</td>
                  <td><span className={`status ${u.role === 'Admin' ? '' : u.role === 'Manager' ? 'ok' : 'neutral'}`}>{u.role}</span></td>
                  <td>{u.managerId ? byId.get(u.managerId)?.displayName : <span className="muted small">— (administrators approve)</span>}</td>
                  <td className="small">{u.departments.length === 0 ? <span className="muted">{u.role === 'Admin' ? 'all (admin)' : 'none'}</span>
                    : Object.entries(groupBy(u.departments)).map(([cid, codes]) => (
                      <div key={cid}><span className="muted">{companyName(Number(cid))}:</span> {codes.join(', ')}</div>
                    ))}</td>
                  <td className="nowrap muted small">{when(u.lastLoginAt)}</td>
                  <td><button className="link">Edit</button></td>
                </tr>
              ))}</tbody>
            </table>
          )}
        </div>
      </div>

      {editing && <UserDialog user={editing === 'new' ? null : editing} users={users ?? []} companies={companies} meId={me.id}
        onClose={() => setEditing(null)}
        onSaved={msg => { setEditing(null); load(); setToast({ kind: 'success', text: msg }) }}
        onError={t => setToast({ kind: 'error', text: t })} />}
      {toastNode}
    </div>
  )
}

function groupBy(depts: DepartmentRef[]): Record<number, string[]> {
  const g: Record<number, string[]> = {}
  for (const d of depts) (g[d.companyId] ??= []).push(d.brandCode)
  return g
}

function UserDialog({ user, users, companies, meId, onClose, onSaved, onError }: {
  user: AppUser | null; users: AppUser[]; companies: Company[]; meId: number
  onClose: () => void; onSaved: (msg: string) => void; onError: (t: string) => void
}) {
  const [f, setF] = useState<UserForm>(() => user
    ? { userName: user.userName, displayName: user.displayName, email: user.email, role: user.role, managerId: user.managerId, active: user.active, password: '', departments: user.departments }
    : { userName: '', displayName: '', email: '', role: 'User', managerId: null, active: true, password: '', departments: [] })
  const [company, setCompany] = useState<number>(companies[0]?.id ?? 0)
  const [brands, setBrands] = useState<Brand[]>([])
  const [owners, setOwners] = useState<Map<string, string[]>>(new Map())
  const [busy, setBusy] = useState(false)
  const set = <K extends keyof UserForm>(k: K, v: UserForm[K]) => setF({ ...f, [k]: v })

  useEffect(() => {
    if (!company) return
    api.brandsOf(company).then(setBrands).catch(() => setBrands([]))
    // Who else already owns each department in this company (helps avoid gaps and doubles).
    const m = new Map<string, string[]>()
    for (const u of users) if (u.active && u.id !== user?.id)
      for (const d of u.departments) if (d.companyId === company) m.set(d.brandCode, [...(m.get(d.brandCode) ?? []), u.displayName])
    setOwners(m)
  }, [company])

  const has = (code: string) => f.departments.some(d => d.companyId === company && d.brandCode === code)
  const toggle = (code: string) => set('departments', has(code)
    ? f.departments.filter(d => !(d.companyId === company && d.brandCode === code))
    : [...f.departments, { companyId: company, brandCode: code }])

  const managers = users.filter(u => u.active && u.id !== user?.id && u.role !== 'User')
  const isSelf = user?.id === meId

  const save = async () => {
    setBusy(true)
    try {
      const body = { ...f, password: f.password || null, email: f.email || null }
      if (user) await api.saveUser(user.id, body); else await api.createUser(body)
      onSaved(user ? `${f.displayName || f.userName} updated.` : `${f.displayName || f.userName} created — they will choose their own password at first sign-in.`)
    } catch (e) { onError((e as Error).message); setBusy(false) }
  }

  return (
    <Modal wide title={user ? `Edit ${user.displayName}` : 'Add user'} onClose={onClose} footer={<>
      {user && !isSelf && (f.active
        ? <button className="danger" onClick={() => set('active', false)} style={{ marginRight: 'auto' }}>Disable user</button>
        : <button onClick={() => set('active', true)} style={{ marginRight: 'auto' }}>Enable user</button>)}
      <button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={busy || !f.userName.trim() || (!user && !f.password)} onClick={save}>{busy ? 'Saving…' : user ? 'Save' : 'Create user'}</button>
    </>}>
      {!f.active && <div className="banner warn">This user is disabled and can’t sign in. Save to apply.</div>}
      <div className="form-grid">
        <label className="field"><span>Full name</span><input value={f.displayName} onChange={e => set('displayName', e.target.value)} autoFocus /></label>
        <label className="field"><span>User name (sign-in)</span><input value={f.userName} onChange={e => set('userName', e.target.value)} placeholder="e.g. sam or sam@company.com" /></label>
        <label className="field"><span>E-mail</span><input value={f.email ?? ''} onChange={e => set('email', e.target.value)} /></label>
        <label className="field"><span>{user ? 'Reset password (optional)' : 'Initial password'}</span>
          <input type="password" value={f.password ?? ''} onChange={e => set('password', e.target.value)} autoComplete="new-password" placeholder="at least 8 characters" />
          <span className="small muted">They must change it at their next sign-in.</span></label>
      </div>
      <div className="form-grid">
        <label className="field"><span>Role</span>
          <select value={f.role} disabled={isSelf} onChange={e => set('role', e.target.value as UserRole)}>
            <option value="User">User — budget owner</option>
            <option value="Manager">Manager — approves team</option>
            <option value="Admin">Administrator — finance</option>
          </select>
          <span className="small muted">{roleHelp[f.role]}</span></label>
        <label className="field"><span>Manager (approves this user’s submissions)</span>
          <select value={f.managerId ?? ''} onChange={e => set('managerId', e.target.value ? Number(e.target.value) : null)}>
            <option value="">— none: administrators approve —</option>
            {managers.map(m => <option key={m.id} value={m.id}>{m.displayName} ({m.role})</option>)}
          </select></label>
      </div>

      <div className="panel" style={{ boxShadow: 'none' }}>
        <div className="panel-head">
          <h3>Departments (cost centers)</h3>
          <span className="small muted">{f.departments.length} selected</span>
          <div className="grow" />
          {companies.length > 1 && (
            <select value={company} onChange={e => setCompany(Number(e.target.value))}>
              {companies.map(c => <option key={c.id} value={c.id}>{c.name} ({f.departments.filter(d => d.companyId === c.id).length})</option>)}
            </select>
          )}
        </div>
        <div className="panel-body" style={{ maxHeight: 260, overflow: 'auto' }}>
          {f.role === 'Admin' && <div className="banner info" style={{ marginBottom: 10 }}>Administrators see every cost center. Departments here only matter if this person also owns a budget.</div>}
          {brands.length === 0 ? <div className="muted small">No cost centers synced for this company yet (Companies → Sync brands & accounts).</div> : (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '6px 16px' }}>
              {brands.map(b => (
                <label key={b.code} className="check" style={{ alignItems: 'flex-start' }}>
                  <input type="checkbox" checked={has(b.code)} onChange={() => toggle(b.code)} style={{ marginTop: 3 }} />
                  <span><strong>{b.code}</strong> <span className="muted">{b.name}</span>{!b.active && <span className="small muted"> (inactive)</span>}
                    {owners.get(b.code) && <div className="small muted">also: {owners.get(b.code)!.join(', ')}</div>}</span>
                </label>
              ))}
            </div>
          )}
        </div>
      </div>
    </Modal>
  )
}
