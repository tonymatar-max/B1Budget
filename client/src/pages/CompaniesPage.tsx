import { useEffect, useState } from 'react'
import { api, getCompanyId, setCompanyId, type Company, type CompanyForm, type FieldMap } from '../api'
import { go } from '../App'
import { MONTHS, when } from '../format'
import { useToast } from '../ui'

const blank: CompanyForm = {
  name: '', currency: '', groupRate: 1, mode: 'ServiceLayer', serviceLayerUrl: 'https://localhost:50000/b1s/v1',
  companyDb: '', userName: 'manager', password: '', ignoreSslErrors: true, dimension: 1, fiscalYearStartMonth: 1,
  scenarioNamePattern: '{brand} {year}', pushMainBudget: true, mainScenarioName: 'Main Budget', fieldMapOverrideJson: null,
}

const toForm = (c: Company): CompanyForm => ({
  name: c.name, currency: c.currency, groupRate: c.groupRate, mode: c.mode, serviceLayerUrl: c.serviceLayerUrl,
  companyDb: c.companyDb, userName: c.userName, password: '', ignoreSslErrors: c.ignoreSslErrors, dimension: c.dimension,
  fiscalYearStartMonth: c.fiscalYearStartMonth, scenarioNamePattern: c.scenarioNamePattern, pushMainBudget: c.pushMainBudget,
  mainScenarioName: c.mainScenarioName, fieldMapOverrideJson: c.fieldMapOverrideJson,
})

export default function CompaniesPage({ selectedId, onChanged }: { selectedId?: number; onChanged: () => void }) {
  const [companies, setCompanies] = useState<Company[] | null>(null)
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [form, setForm] = useState<CompanyForm>(blank)
  const [busy, setBusy] = useState<string | null>(null)
  const [fields, setFields] = useState<FieldMap | null>(null)
  const [counts, setCounts] = useState<{ brands: number; accounts: number } | null>(null)
  const [, setToast, toastNode] = useToast()

  const load = async (select?: number | 'new') => {
    const list = await api.companies()
    setCompanies(list)
    const target = select ?? selectedId ?? getCompanyId() ?? list[0]?.id
    open(target === 'new' ? 'new' : list.find(c => c.id === target) ?? list[0])
  }
  useEffect(() => { load().catch(e => setToast({ kind: 'error', text: e.message })) }, [])

  const open = (c: Company | 'new' | undefined) => {
    setFields(null)
    setCounts(null)
    if (!c) return
    if (c === 'new') { setEditing('new'); setForm(blank); return }
    setEditing(c.id)
    setForm(toForm(c))
    // Brand/account counts are per company; read them with that company's header by switching temporarily.
    if (c.id === getCompanyId()) Promise.all([api.brands(), api.accounts()]).then(([b, a]) => setCounts({ brands: b.length, accounts: a.length }))
  }

  const current = companies?.find(c => c.id === editing) ?? null
  const set = <K extends keyof CompanyForm>(k: K, v: CompanyForm[K]) => setForm({ ...form, [k]: v })

  const act = async (label: string, fn: () => Promise<string>) => {
    setBusy(label)
    try { setToast({ kind: 'success', text: await fn() }) }
    catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
    finally { setBusy(null) }
  }

  const save = () => act('save', async () => {
    const body = { ...form, password: form.password || null }
    const saved = editing === 'new' ? await api.createCompany(body) : await api.saveCompany(editing as number, body)
    await load(saved.id)
    onChanged()
    return `${saved.name} saved.` + (saved.masterDataSyncedAt ? '' : ' Sync brands & accounts next.')
  })

  const remove = () => act('delete', async () => {
    if (!current || !confirm(`Remove company "${current.name}"? Its brands and accounts cache is deleted too.`)) return 'Cancelled.'
    await api.deleteCompany(current.id)
    await load()
    onChanged()
    return `${current.name} removed.`
  })

  const live = form.mode === 'ServiceLayer'
  const pattern = form.scenarioNamePattern.replace(/\{brand\}/gi, 'NIKE').replace(/\{year\}/gi, String(new Date().getFullYear()))
  const isNew = editing === 'new'

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Companies</h1>
          <span className="muted">Each SAP B1 company database has its own connection, brands, accounts and budgets. Switch the active company from the top bar.</span>
        </div>
        <button className="primary" onClick={() => open('new')}>Add company</button>
      </div>

      <div className="editor">
        <div className="panel brand-list">
          {companies?.map(c => (
            <button key={c.id} className={`brand-item${c.id === editing ? ' active' : ''}`} onClick={() => open(c)}>
              <span className="nm">
                <strong>{c.name}</strong>
                <span>{c.mode === 'Mock' ? 'Mock (demo data)' : c.companyDb || 'not configured'}</span>
              </span>
              <span className="amt">{c.currency || '—'}<br /><span className="small">{c.budgetCount} budget{c.budgetCount === 1 ? '' : 's'}</span></span>
            </button>
          ))}
          {isNew && <button className="brand-item active"><span className="nm"><strong>New company</strong><span>unsaved</span></span></button>}
        </div>

        {editing !== null && (
          <div className="stack">
            <div className="panel">
              <div className="panel-head">
                <h2>{isNew ? 'New company' : form.name}</h2>
                {current && current.id === getCompanyId() && <span className="status">Active</span>}
                <div className="grow" />
                {current && current.id !== getCompanyId() && (
                  <button onClick={() => { setCompanyId(current.id); go({ page: 'companies', id: current.id }) }}>Make active</button>
                )}
                {current && <button className="danger" disabled={!!busy || current.budgetCount > 0}
                  title={current.budgetCount > 0 ? 'Companies with budgets cannot be removed' : ''} onClick={remove}>Remove</button>}
                <button className="primary" disabled={!!busy || !form.name.trim()} onClick={save}>{busy === 'save' ? 'Saving…' : isNew ? 'Create company' : 'Save'}</button>
              </div>
              <div className="panel-body stack">
                <div className="form-grid">
                  <label className="field"><span>Name</span><input value={form.name} onChange={e => set('name', e.target.value)} placeholder="Kuwait, UAE Trading…" /></label>
                  <label className="field"><span>Local currency</span><input value={form.currency} onChange={e => set('currency', e.target.value)} placeholder="KWD" maxLength={5} /></label>
                  <label className="field"><span>Rate to group currency</span>
                    <input type="number" step="0.0001" min="0" value={form.groupRate} onChange={e => set('groupRate', Number(e.target.value))} />
                    <span className="small muted">Group report amount = local amount × rate.</span></label>
                </div>
              </div>
            </div>

            <div className="panel">
              <div className="panel-head"><h2>Connection</h2><div className="grow" />
                <div className="seg">
                  <button className={!live ? 'on' : ''} onClick={() => set('mode', 'Mock')}>Mock (demo data)</button>
                  <button className={live ? 'on' : ''} onClick={() => set('mode', 'ServiceLayer')}>SAP B1 Service Layer</button>
                </div>
              </div>
              <div className="panel-body stack">
                {!live && <div className="banner warn">Mock mode uses built-in demo brands, accounts and actuals. Pushes are simulated.</div>}
                <div className="form-grid" style={{ opacity: live ? 1 : .55 }}>
                  <label className="field" style={{ gridColumn: 'span 2' }}><span>Service Layer URL</span>
                    <input value={form.serviceLayerUrl} onChange={e => set('serviceLayerUrl', e.target.value)} placeholder="https://b1server:50000/b1s/v1" /></label>
                  <label className="field"><span>Company database</span><input value={form.companyDb} onChange={e => set('companyDb', e.target.value)} placeholder="SBODEMOUS" /></label>
                  <label className="field"><span>User</span><input value={form.userName} onChange={e => set('userName', e.target.value)} /></label>
                  <label className="field"><span>Password {current?.hasPassword && <em className="muted">(stored, encrypted — leave blank to keep)</em>}</span>
                    <input type="password" value={form.password ?? ''} onChange={e => set('password', e.target.value)} autoComplete="new-password" /></label>
                  <label className="check" style={{ alignSelf: 'end' }}><input type="checkbox" checked={form.ignoreSslErrors} onChange={e => set('ignoreSslErrors', e.target.checked)} /> Accept self-signed certificate</label>
                </div>
                {current && <div className="row">
                  <button disabled={!!busy} onClick={() => act('test', async () => (await api.testConnection(current.id)).message)}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
                  <span className="small muted">Uses the saved settings — save first.</span>
                </div>}
              </div>
            </div>

            <div className="panel">
              <div className="panel-head"><h2>Budget structure</h2></div>
              <div className="panel-body stack">
                <div className="form-grid">
                  <label className="field"><span>Brand dimension (cost accounting)</span>
                    <select value={form.dimension} onChange={e => set('dimension', Number(e.target.value))}>
                      {[1, 2, 3, 4, 5].map(d => <option key={d} value={d}>Dimension {d}{d === 1 ? ' (ProfitCode)' : ` (OcrCode${d})`}</option>)}
                    </select></label>
                  <label className="field"><span>Fiscal year starts in</span>
                    <select value={form.fiscalYearStartMonth} onChange={e => set('fiscalYearStartMonth', Number(e.target.value))}>
                      {MONTHS.map((m, i) => <option key={i} value={i + 1}>{m}</option>)}
                    </select></label>
                  <label className="field"><span>B1 budget scenario name</span>
                    <input value={form.scenarioNamePattern} onChange={e => set('scenarioNamePattern', e.target.value)} />
                    <span className="small muted">Tokens {'{brand}'} and {'{year}'} — e.g. “{pattern}”.</span></label>
                  <label className="field"><span>Main budget scenario (all brands)</span>
                    <input value={form.mainScenarioName} disabled={!form.pushMainBudget} onChange={e => set('mainScenarioName', e.target.value)} />
                    <label className="check small"><input type="checkbox" checked={form.pushMainBudget} onChange={e => set('pushMainBudget', e.target.checked)} />
                      Also push the total of all brands per account here.</label></label>
                </div>
                {current && <div className="row">
                  <button disabled={!!busy} onClick={() => act('sync', async () => {
                    const r = await api.syncMasterData(current.id)
                    await load(current.id)
                    return `${current.name}: loaded ${r.brands} brands (dimension ${current.dimension}) and ${r.accounts} P&L accounts.`
                  })}>{busy === 'sync' ? 'Syncing…' : 'Sync brands & accounts from B1'}</button>
                  <span className="small muted">
                    {counts && `${counts.brands} brands · ${counts.accounts} P&L accounts · `}last synced {when(current.masterDataSyncedAt)}
                  </span>
                </div>}
              </div>
            </div>

            {current && (
              <div className="panel">
                <div className="panel-head"><h2>Advanced — Service Layer budget fields</h2></div>
                <div className="panel-body stack">
                  <span className="muted small">Budget property names are read from this company’s <code>$metadata</code>. Override one only if detection picks the wrong property.</span>
                  <div className="row">
                    <button disabled={!!busy} onClick={() => act('fields', async () => { setFields(await api.budgetFields(current.id)); return 'Budget field map loaded.' })}>
                      {busy === 'fields' ? 'Reading $metadata…' : 'Detect budget fields'}</button>
                  </div>
                  {fields && <>
                    <div className="table-wrap">
                      <table className="data">
                        <thead><tr><th>Role</th><th>Service Layer property</th></tr></thead>
                        <tbody>{Object.entries(fields).filter(([k]) => k !== 'notes' && k !== 'fromMetadata').map(([k, v]) =>
                          <tr key={k}><td>{k}</td><td className="code">{v === null ? '— (not used)' : String(v)}</td></tr>)}</tbody>
                      </table>
                    </div>
                    {fields.notes.length > 0 && <div className="banner info">{fields.notes.map((n, i) => <div key={i}>{n}</div>)}</div>}
                  </>}
                  <label className="field"><span>Field-map override (JSON)</span>
                    <textarea value={form.fieldMapOverrideJson ?? ''} onChange={e => set('fieldMapOverrideJson', e.target.value || null)}
                      placeholder='{"LineDebit": "BudgetTotDebit", "LineCredit": "BudgetTotCredit"}' />
                  </label>
                </div>
              </div>
            )}
          </div>
        )}
      </div>
      {toastNode}
    </div>
  )
}
