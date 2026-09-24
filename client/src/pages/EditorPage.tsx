import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { api, getCompanyId, setCompanyId, type Account, type Brand, type DeptEvent, type DeptView, type Line, type PushRun, type ReconcileRow, type Settings, type VersionSummary } from '../api'
import { go } from '../App'
import { useAuth } from '../auth'
import { DeptActions, DeptPill } from '../DeptActions'
import { fmt, fmt2, parseAmount, short, sum, when } from '../format'
import { Empty, Modal, PushPill, VersionPill, revLabel, useToast } from '../ui'

type Lines = Record<string, Line[]>   // brand → lines

export default function EditorPage({ id, settings, initialBrand }: { id: number; settings: Settings | null; initialBrand?: string }) {
  const { isAdmin } = useAuth()
  const [depts, setDepts] = useState<DeptView[]>([])
  const [events, setEvents] = useState<DeptEvent[] | null>(null)
  const [version, setVersion] = useState<VersionSummary | null>(null)
  const [revisions, setRevisions] = useState<VersionSummary[]>([])
  const [lines, setLines] = useState<Lines>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [brands, setBrands] = useState<Brand[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [brand, setBrand] = useState<string>('')
  const [periodLabels, setPeriodLabels] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [dialog, setDialog] = useState<'seed' | 'push' | 'reconcile' | 'rename' | null>(null)
  const [, setToast, toastNode] = useToast()
  const fileRef = useRef<HTMLInputElement>(null)

  const load = async () => {
    try {
      // A budget belongs to one company — follow it if the link came from another company's context.
      const own = await api.version(id)
      if (own.version.companyId !== getCompanyId()) { setCompanyId(own.version.companyId); return }
      const [v, b, a, revs, ds] = await Promise.all([Promise.resolve(own), api.brands(), api.accounts(), api.revisions(id), api.departments(id)])
      setVersion(v.version)
      setRevisions(revs)
      setDepts(ds)
      const grouped: Lines = {}
      for (const l of v.lines) (grouped[l.brandCode] ??= []).push(l)
      setLines(grouped)
      setDirty(new Set())
      setBrands(b)
      setAccounts(a)
      setBrand(cur => cur || (initialBrand && (b.some(x => x.code === initialBrand) || grouped[initialBrand]) ? initialBrand : '')
        || ds.find(d => d.canEdit)?.brand || b.find(x => x.active)?.code || b[0]?.code || '')
    } catch (e) { setError((e as Error).message) }
  }
  useEffect(() => { load() }, [id])

  // Period labels follow the fiscal-year start month from settings.
  useEffect(() => {
    if (!version || !settings) return
    setPeriodLabels(Array.from({ length: 12 }, (_, i) =>
      new Date(version.fiscalYear, settings.fiscalYearStartMonth - 1 + i, 1).toLocaleDateString(undefined, { month: 'short', year: '2-digit' })))
  }, [version, settings])

  useEffect(() => {
    if (dirty.size === 0) return
    const h = (e: BeforeUnloadEvent) => { e.preventDefault() }
    window.addEventListener('beforeunload', h)
    return () => window.removeEventListener('beforeunload', h)
  }, [dirty])

  // Approval history of the selected department.
  useEffect(() => {
    setEvents(null)
    if (brand) api.deptEvents(id, brand).then(setEvents).catch(() => setEvents([]))
  }, [id, brand, depts])

  const accountMap = useMemo(() => new Map(accounts.map(a => [a.code, a])), [accounts])
  const versionDraft = version?.status === 'Draft'
  const dept = depts.find(d => d.brand === brand)
  // A department with no workflow record yet is a plain draft.
  const readOnly = !versionDraft || (dept ? !dept.canEdit : false)
  const anyEditable = versionDraft && (isAdmin || depts.some(d => d.canEdit))
  const brandLines = lines[brand] ?? []

  const brandNet = (code: string) => {
    let n = 0
    // Accounts not in the chart of accounts count toward neither revenue nor expenses.
    for (const l of lines[code] ?? []) {
      const k = accountMap.get(l.accountCode)?.kind
      n += (k === 'Revenue' ? 1 : k === 'Expense' ? -1 : 0) * sum(l.amounts)
    }
    return n
  }

  const updateBrand = (fn: (ls: Line[]) => Line[]) => {
    setLines(prev => ({ ...prev, [brand]: fn(prev[brand] ?? []) }))
    setDirty(prev => new Set(prev).add(brand))
  }

  const setAmount = (account: string, period: number, value: number) =>
    updateBrand(ls => ls.map(l => l.accountCode !== account ? l : { ...l, amounts: l.amounts.map((a, i) => i === period ? value : a) }))

  // Typing an annual total spreads it: proportionally to the current phasing, or evenly if the row is empty.
  const setTotal = (account: string, total: number) =>
    updateBrand(ls => ls.map(l => {
      if (l.accountCode !== account) return l
      const cur = sum(l.amounts)
      let amounts = cur > 0 ? l.amounts.map(a => Math.round(a / cur * total * 100) / 100) : Array(12).fill(Math.floor(total / 12 * 100) / 100)
      const diff = Math.round((total - sum(amounts)) * 100) / 100
      amounts = amounts.map((a, i) => i === 11 ? Math.round((a + diff) * 100) / 100 : a)
      return { ...l, amounts }
    }))

  const addAccounts = (codes: string[]) =>
    updateBrand(ls => [...ls, ...codes.filter(c => !ls.some(l => l.accountCode === c))
      .map(c => ({ brandCode: brand, accountCode: c, amounts: Array(12).fill(0) }))])

  const removeAccount = (code: string) => updateBrand(ls => ls.filter(l => l.accountCode !== code))

  const saveAll = async (): Promise<boolean> => {
    if (dirty.size === 0) return true
    setSaving(true)
    try {
      for (const b of dirty) await api.saveBrandLines(id, b, lines[b] ?? [])
      setDirty(new Set())
      setToast({ kind: 'success', text: `Saved ${dirty.size} brand${dirty.size > 1 ? 's' : ''}.` })
      const [v, ds] = await Promise.all([api.version(id), api.departments(id)])
      setVersion(v.version)
      setDepts(ds)
      return true
    } catch (e) {
      setToast({ kind: 'error', text: (e as Error).message })
      return false
    } finally { setSaving(false) }
  }

  const approve = async () => {
    const prev = revisions.find(r => r.status === 'Approved' || r.status === 'Pushed')
    if (!confirm(`Approve ${version ? revLabel(version) : 'this budget'}? It will be locked — further changes need a new revision.`
      + (prev ? `\n\nRev ${prev.revisionNo} will be marked as superseded.` : ''))) return
    if (!(await saveAll())) return
    try { await api.approve(id); await load() } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  const revise = async () => {
    try {
      const r = await api.revise(id)
      go({ page: 'editor', id: r.id })
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  const discardDraft = async () => {
    if (!version || !confirm(`Discard draft Rev ${version.revisionNo}? This cannot be undone.`)) return
    try {
      await api.deleteVersion(id)
      const prev = revisions.filter(r => r.id !== id).at(-1)
      go(prev ? { page: 'editor', id: prev.id } : { page: 'budgets' })
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  const onImport = async (f: File | undefined) => {
    if (!f) return
    if (dirty.size > 0 && !confirm('You have unsaved changes that the import will discard for the brands in the file. Continue?')) return
    try {
      const r = await api.importExcel(id, f)
      await load()
      setToast({
        kind: r.errors.length ? 'info' : 'success',
        text: `Imported ${r.imported} lines for ${r.brands} brands.` + (r.errors.length ? ` ${r.errors.length} issue(s): ${r.errors.slice(0, 3).join(' ')}` : ''),
      })
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
    if (fileRef.current) fileRef.current.value = ''
  }

  if (error) return <div className="page"><div className="banner error">{error}</div></div>
  if (!version) return <div className="page"><div className="empty">Loading…</div></div>

  const brandCodes = new Set(brands.map(b => b.code))
  const orphanCount = Object.values(lines).flat().filter(l => !accountMap.has(l.accountCode) || !brandCodes.has(l.brandCode)).length
  const removeOrphans = async () => {
    if (!(await saveAll())) return
    try {
      const r = await api.removeOrphans(id)
      await load()
      setToast({ kind: 'success', text: `Removed ${r.removed} line${r.removed === 1 ? '' : 's'}.` })
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  const revenueLines = brandLines.filter(l => accountMap.get(l.accountCode)?.kind === 'Revenue').sort((a, b) => a.accountCode.localeCompare(b.accountCode))
  const expenseLines = brandLines.filter(l => accountMap.get(l.accountCode)?.kind === 'Expense').sort((a, b) => a.accountCode.localeCompare(b.accountCode))
  const unmappedLines = brandLines.filter(l => { const k = accountMap.get(l.accountCode)?.kind; return k !== 'Revenue' && k !== 'Expense' })
    .sort((a, b) => a.accountCode.localeCompare(b.accountCode))
  const colSum = (ls: Line[], i: number) => ls.reduce((s, l) => s + l.amounts[i], 0)
  const unused = accounts.filter(a => !brandLines.some(l => l.accountCode === a.code))
  // Cost centers of this company, plus any brand code the budget has lines for that isn't one of them
  // (e.g. left over from demo data) — shown and flagged, never silently hidden.
  const strayBrands: Brand[] = Object.keys(lines).filter(c => (lines[c]?.length ?? 0) > 0 && !brandCodes.has(c)).sort()
    .map(c => ({ code: c, name: '', active: false }))
  const visibleBrands = [...brands.filter(b => b.active || (lines[b.code]?.length ?? 0) > 0), ...strayBrands]
  const brandName = brands.find(b => b.code === brand)?.name ?? brand

  const renderRows = (ls: Line[]) => ls.map(l => {
    const acc = accountMap.get(l.accountCode)
    return (
      <tr key={l.accountCode}>
        <td className="acct" title={`${l.accountCode} ${acc?.name ?? ''}`}>
          <div className="an">{acc?.name ?? '(unknown account)'}</div>
          <div className="ac">{acc?.displayCode ?? l.accountCode}</div>
        </td>
        {l.amounts.map((a, i) => (
          <td key={i}><Cell value={a} disabled={readOnly} onCommit={v => setAmount(l.accountCode, i, v)} /></td>
        ))}
        <td className="tot"><Cell value={sum(l.amounts)} disabled={readOnly} onCommit={v => setTotal(l.accountCode, v)} title="Type an annual total to spread it across the months" /></td>
        <td className="act">{!readOnly && <button className="icon link" title="Remove line" onClick={() => removeAccount(l.accountCode)}>✕</button>}</td>
      </tr>
    )
  })

  const subtotal = (label: string, ls: Line[]) => (
    <tr className="subtotal">
      <td className="acct">{label}</td>
      {Array.from({ length: 12 }, (_, i) => <td key={i}>{fmt(colSum(ls, i))}</td>)}
      <td>{fmt(ls.reduce((s, l) => s + sum(l.amounts), 0))}</td><td />
    </tr>
  )

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <div className="crumb"><button className="link" onClick={() => go({ page: 'budgets' })}>Budgets</button> / {version.fiscalYear}</div>
          <div className="row">
            <h1>{version.name}</h1>
            <select value={id} onChange={e => go({ page: 'editor', id: Number(e.target.value) })} title="Revisions of this budget"
              style={{ fontWeight: 600 }}>
              {revisions.map(r => <option key={r.id} value={r.id}>Rev {r.revisionNo} · {r.status === 'Pushed' ? 'In SAP B1' : r.status}</option>)}
            </select>
            <VersionPill status={version.status} />
            {isAdmin && <button className="link small" onClick={() => setDialog('rename')}>Rename</button>}
          </div>
          <span className="small muted">
            FY {version.fiscalYear} · {version.brandCount} brands · {version.lineCount} lines · updated {when(version.updatedAt)}
            {version.lastPushedAt && <> · pushed {when(version.lastPushedAt)}</>}
          </span>
        </div>
        <div className="row">
          <a className="btn" href={api.exportUrl(id)}>Export Excel</a>
          {anyEditable && <>
            <button onClick={() => fileRef.current?.click()}>Import Excel</button>
            <input ref={fileRef} type="file" accept=".xlsx" hidden onChange={e => onImport(e.target.files?.[0])} />
            <button onClick={() => setDialog('seed')}>Seed from actuals</button>
            <button disabled={saving || dirty.size === 0} onClick={saveAll}>{saving ? 'Saving…' : `Save${dirty.size ? ` (${dirty.size})` : ''}`}</button>
          </>}
          {isAdmin && versionDraft && <>
            {version.revisionNo > 1 && <button className="danger" onClick={discardDraft}>Discard draft</button>}
            <button onClick={() => go({ page: 'approvals', v: id })}>Global view</button>
            <button className="primary" disabled={saving || version.lineCount + dirty.size === 0} onClick={approve}
              title="Needs every department to be approved first">Approve budget</button>
          </>}
          {version.parentVersionId && (
            <button onClick={() => go({ page: 'compare', a: version.parentVersionId!, b: id })}>
              Changes vs Rev {revisions.find(r => r.id === version.parentVersionId)?.revisionNo ?? '?'}
            </button>
          )}
          {isAdmin && (version.status === 'Approved' || version.status === 'Pushed') && <>
            <button onClick={() => setDialog('reconcile')}>Compare with B1</button>
            <button onClick={revise}>Create revision</button>
            <button className="primary" onClick={() => setDialog('push')}>Push to SAP B1</button>
          </>}
        </div>
      </div>

      {orphanCount > 0 && (
        <div className="banner warn row">
          <span className="grow">
            {orphanCount} line{orphanCount > 1 ? 's use' : ' uses'} an account or brand that isn’t in the synced SAP B1 master data
            (e.g. a balance-sheet account). They inflate the totals and would fail on push.
          </span>
          {isAdmin && versionDraft
            ? <button onClick={removeOrphans}>Remove {orphanCount} line{orphanCount > 1 ? 's' : ''}</button>
            : <span className="small">{isAdmin ? 'Create a revision to remove them.' : 'Ask an administrator to remove them.'}</span>}
        </div>
      )}
      {version.status === 'Approved' && <div className="banner info">Approved — this revision is locked.{isAdmin ? ' Push it to update the budgets in SAP B1, or create a revision to make changes.' : ''}</div>}
      {version.status === 'Superseded' && (() => {
        const current = revisions.filter(r => r.status === 'Approved' || r.status === 'Pushed').at(-1)
        const draft = revisions.find(r => r.status === 'Draft')
        return (
          <div className="banner warn row">
            <span className="grow">Rev {version.revisionNo} is superseded (read-only history){current ? ` — Rev ${current.revisionNo} is the current budget.` : '.'}</span>
            {current && <button onClick={() => go({ page: 'editor', id: current.id })}>Open Rev {current.revisionNo}</button>}
            {current && <button onClick={() => go({ page: 'compare', a: id, b: current.id })}>What changed</button>}
            {!current && draft && <button onClick={() => go({ page: 'editor', id: draft.id })}>Open draft Rev {draft.revisionNo}</button>}
          </div>
        )
      })()}
      {!isAdmin && versionDraft && depts.length === 0 && (
        <div className="banner info">None of your departments are in this budget yet.</div>
      )}
      {version.status === 'Draft' && version.revisionNo > 1 && (
        <div className="banner info">Draft revision {version.revisionNo} — copied from Rev {revisions.find(r => r.id === version.parentVersionId)?.revisionNo}. The approved revision stays in effect until this one is approved and pushed.</div>
      )}

      {visibleBrands.length === 0 ? (
        <div className="panel"><Empty>
          <strong>No brands loaded</strong>
          <span>Sync brands & accounts under Companies to load the distribution rules for dimension {settings?.dimension ?? 1}.</span>
          <button className="primary" onClick={() => go({ page: 'companies' })}>Open settings</button>
        </Empty></div>
      ) : (
        <div className="editor">
          <div className="panel brand-list">
            {visibleBrands.map(b => {
              const net = brandNet(b.code)
              return (
                <button key={b.code} className={`brand-item${b.code === brand ? ' active' : ''}`} onClick={() => setBrand(b.code)}>
                  <span className="nm">
                    <strong>{b.code}{dirty.has(b.code) && <span className="dot-dirty" title="Unsaved changes" />}</strong>
                    {brandCodes.has(b.code)
                      ? <span>{b.name}{!b.active && ' (inactive)'}</span>
                      : <span className="warn">not a cost center in this company</span>}
                    {versionDraft && (() => {
                      const st = depts.find(d => d.brand === b.code)?.status
                      return st && st !== 'Draft' ? <span style={{ marginTop: 3 }}><DeptPill status={st} /></span> : null
                    })()}
                  </span>
                  <span className="amt">
                    {(lines[b.code]?.length ?? 0) > 0 ? <>{short(net)}<br /><span className="small">{lines[b.code].length} lines</span></> : <span className="small">—</span>}
                  </span>
                </button>
              )
            })}
          </div>

          <div className="panel">
            <div className="panel-head">
              <h2>{brandName} <span className="muted small">· {brand}</span></h2>
              {versionDraft && <DeptPill status={dept?.status ?? 'Draft'} />}
              {dept && dept.owners.length > 0 && <span className="small muted">Owner: {dept.owners.join(', ')}</span>}
              <div className="grow" />
              {!readOnly && unused.length > 0 && <AddAccount accounts={unused} onAdd={addAccounts} />}
              {versionDraft && (
                <DeptActions versionId={id}
                  dept={dept ?? { brand, brandName, owners: [], status: 'Draft', submittedBy: null, submittedAt: null, approver: null, decidedBy: null,
                    decidedAt: null, comment: null, lineCount: brandLines.length, revenue: 0, expense: 0,
                    canEdit: true, canSubmit: true, canApprove: isAdmin, canReject: false, canReopen: false }}
                  beforeSubmit={saveAll}
                  onDone={async msg => { setToast({ kind: 'success', text: msg }); setDepts(await api.departments(id)) }}
                  onError={t => setToast({ kind: 'error', text: t })} />
              )}
            </div>
            {versionDraft && dept && dept.status !== 'Draft' && (
              <div className={`banner ${dept.status === 'Rejected' ? 'error' : dept.status === 'Approved' ? 'success' : 'warn'}`} style={{ margin: '12px 16px 0' }}>
                {dept.status === 'Submitted' && <>Submitted by {dept.submittedBy} {when(dept.submittedAt)} — waiting for {dept.approver}. Locked until it is approved, rejected or reopened.</>}
                {dept.status === 'Approved' && <>Approved by {dept.decidedBy} {when(dept.decidedAt)}{dept.comment ? ` — “${dept.comment}”` : ''}. Locked; it counts in the company budget.</>}
                {dept.status === 'Rejected' && <>Rejected by {dept.decidedBy} {when(dept.decidedAt)}: “{dept.comment}”. Make the changes and submit again.</>}
              </div>
            )}
            {brandLines.length === 0 ? (
              <Empty>
                <strong>No budget lines for {brandName}</strong>
                <span>Add the G/L accounts this brand should budget, or seed from last year’s actuals.</span>
                {!readOnly && <div className="row">
                  <button onClick={() => addAccounts(accounts.map(a => a.code))}>Add all {accounts.length} P&amp;L accounts</button>
                  <button onClick={() => setDialog('seed')}>Seed from actuals</button>
                </div>}
              </Empty>
            ) : (
              <div className="table-wrap" style={{ maxHeight: 'calc(100vh - 260px)' }}>
                <table className="grid">
                  <thead>
                    <tr>
                      <th className="acct">Account</th>
                      {periodLabels.map((p, i) => <th key={i}>{p}</th>)}
                      <th>Total</th><th />
                    </tr>
                  </thead>
                  <tbody>
                    {revenueLines.length > 0 && <>
                      <tr className="section"><td className="acct">Revenue</td><td colSpan={14} /></tr>
                      {renderRows(revenueLines)}
                      {subtotal('Total revenue', revenueLines)}
                    </>}
                    {expenseLines.length > 0 && <>
                      <tr className="section"><td className="acct">Expenses</td><td colSpan={14} /></tr>
                      {renderRows(expenseLines)}
                      {subtotal('Total expenses', expenseLines)}
                    </>}
                    {unmappedLines.length > 0 && <>
                      <tr className="section"><td className="acct">Not in chart of accounts</td><td colSpan={14} /></tr>
                      {renderRows(unmappedLines)}
                      {subtotal('Not counted in net', unmappedLines)}
                    </>}
                    <tr className="net">
                      <td className="acct">Net contribution</td>
                      {Array.from({ length: 12 }, (_, i) => <td key={i}>{fmt(colSum(revenueLines, i) - colSum(expenseLines, i))}</td>)}
                      <td>{fmt(brandNet(brand))}</td><td />
                    </tr>
                  </tbody>
                </table>
              </div>
            )}
            {events && events.length > 0 && (
              <div style={{ borderTop: '1px solid var(--line)', padding: '10px 16px 12px' }}>
                <h3 style={{ marginBottom: 6 }}>Approval history</h3>
                <div className="stack" style={{ gap: 4 }}>
                  {events.map((e, i) => (
                    <div key={i} className="small">
                      <span className={`status ${e.action === 'Approved' ? 'ok' : e.action === 'Rejected' ? 'bad' : e.action === 'Submitted' ? 'warn' : 'neutral'}`}
                        style={{ minWidth: 84 }}>{e.action}</span>
                      {' '}<strong>{e.user}</strong> <span className="muted">{when(e.at)}</span>
                      {e.comment && <span> — “{e.comment}”</span>}
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {dialog === 'seed' && <SeedDialog versionId={id} year={version.fiscalYear} brands={visibleBrands} current={brand}
        onClose={() => setDialog(null)}
        onDone={async msg => { setDialog(null); await load(); setToast({ kind: 'success', text: msg }) }}
        onError={t => setToast({ kind: 'error', text: t })} hasDirty={dirty.size > 0} />}
      {dialog === 'push' && <PushDialog version={version} brands={Object.keys(lines).filter(b => lines[b].length)} settings={settings}
        onClose={() => { setDialog(null); load() }} />}
      {dialog === 'reconcile' && <ReconcileDialog versionId={id} onClose={() => setDialog(null)} />}
      {dialog === 'rename' && <RenameDialog version={version} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); load() }} />}
      {toastNode}
    </div>
  )
}

/** Numeric grid cell: shows formatted value, edits raw; Enter/↑/↓ move between rows. */
function Cell({ value, onCommit, disabled, title }: { value: number; onCommit: (v: number) => void; disabled?: boolean; title?: string }) {
  const [draft, setDraft] = useState<string | null>(null)
  const ref = useRef<HTMLInputElement>(null)
  const selectOnRender = useRef(false)
  const swallowMouseUp = useRef(false)
  const invalid = draft !== null && parseAmount(draft) === null

  // Select the raw value once React has swapped the formatted text for it, so typing replaces it.
  useLayoutEffect(() => {
    if (selectOnRender.current && draft !== null) { ref.current?.select(); selectOnRender.current = false }
  }, [draft])

  const cancelled = useRef(false)
  const commit = () => {
    if (cancelled.current) { cancelled.current = false; setDraft(null); return }
    if (draft === null) return
    const v = parseAmount(draft)
    if (v !== null && v !== value) onCommit(v)
    setDraft(null)
  }

  /** Focus the same column in the next/previous editable row; false if there is none. */
  const move = (el: HTMLInputElement, dRow: number): boolean => {
    const td = el.closest('td')!
    const col = Array.from(td.parentElement!.children).indexOf(td)
    let tr = td.parentElement as HTMLTableRowElement | null
    for (;;) {
      tr = (dRow > 0 ? tr?.nextElementSibling : tr?.previousElementSibling) as HTMLTableRowElement | null
      if (!tr) return false
      const input = tr.children[col]?.querySelector('input')
      if (input) { (input as HTMLInputElement).focus(); return true }
    }
  }

  return (
    <input
      ref={ref}
      className={`cell${invalid ? ' invalid' : ''}`}
      value={draft ?? (value === 0 ? '' : fmt(value))}
      placeholder={disabled ? '' : '0'}
      disabled={disabled}
      title={title}
      inputMode="decimal"
      onMouseDown={e => { if (document.activeElement !== e.currentTarget) swallowMouseUp.current = true }}
      // The mouseup that follows a focusing click would collapse the selection — skip it once.
      onMouseUp={e => { if (swallowMouseUp.current) { e.preventDefault(); swallowMouseUp.current = false } }}
      onFocus={() => { selectOnRender.current = true; setDraft(value === 0 ? '' : String(value)) }}
      onChange={e => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={e => {
        // Moving focus blurs this cell, and blur is what commits the edit.
        if (e.key === 'Enter' || e.key === 'ArrowDown') { e.preventDefault(); if (!move(e.currentTarget, 1)) commit() }
        else if (e.key === 'ArrowUp') { e.preventDefault(); if (!move(e.currentTarget, -1)) commit() }
        else if (e.key === 'Escape') { cancelled.current = true; e.currentTarget.blur() }
      }}
    />
  )
}

function AddAccount({ accounts, onAdd }: { accounts: Account[]; onAdd: (codes: string[]) => void }) {
  const [val, setVal] = useState('')
  return (
    <div className="row">
      <select value={val} onChange={e => setVal(e.target.value)} style={{ maxWidth: 280 }}>
        <option value="">Add G/L account…</option>
        {(['Revenue', 'Expense'] as const).map(k => (
          <optgroup key={k} label={k === 'Revenue' ? 'Revenue' : 'Expenses'}>
            {accounts.filter(a => (a.kind === 'Revenue') === (k === 'Revenue')).map(a =>
              <option key={a.code} value={a.code}>{a.displayCode ?? a.code} · {a.name}</option>)}
          </optgroup>
        ))}
      </select>
      <button disabled={!val} onClick={() => { onAdd([val]); setVal('') }}>Add</button>
      <button className="link small" onClick={() => onAdd(accounts.map(a => a.code))}>Add all ({accounts.length})</button>
    </div>
  )
}

function SeedDialog({ versionId, year, brands, current, onClose, onDone, onError, hasDirty }: {
  versionId: number; year: number; brands: Brand[]; current: string; hasDirty: boolean
  onClose: () => void; onDone: (msg: string) => void; onError: (t: string) => void
}) {
  const [source, setSource] = useState(year - 1)
  const [uplift, setUplift] = useState(5)
  const [scope, setScope] = useState<'all' | 'current'>('all')
  const [overwrite, setOverwrite] = useState(false)
  const [busy, setBusy] = useState(false)

  const run = async () => {
    setBusy(true)
    try {
      const r = await api.seed(versionId, { sourceFiscalYear: source, upliftPercent: uplift, brands: scope === 'current' ? [current] : undefined, overwrite })
      onDone(`Seeded from FY ${source} actuals ${uplift >= 0 ? '+' : ''}${uplift}%: ${r.added} lines added, ${r.replaced} replaced.`)
    } catch (e) { onError((e as Error).message); setBusy(false) }
  }

  return (
    <Modal title="Seed from actuals" onClose={onClose} footer={<>
      <button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={busy} onClick={run}>{busy ? 'Reading actuals…' : 'Seed budget'}</button>
    </>}>
      <span className="muted">Creates budget lines from posted journal entries (by brand, account and month) of a previous fiscal year, adjusted by a growth %.</span>
      {hasDirty && <div className="banner warn">Save your changes first — seeding reloads the budget from the server.</div>}
      <div className="form-grid">
        <label className="field"><span>Source fiscal year</span><input type="number" value={source} onChange={e => setSource(Number(e.target.value))} /></label>
        <label className="field"><span>Uplift %</span><input type="number" step="0.5" value={uplift} onChange={e => setUplift(Number(e.target.value))} /></label>
      </div>
      <div className="row">
        <div className="seg">
          <button className={scope === 'all' ? 'on' : ''} onClick={() => setScope('all')}>All {brands.length} brands</button>
          <button className={scope === 'current' ? 'on' : ''} onClick={() => setScope('current')}>Only {current}</button>
        </div>
      </div>
      <label className="check"><input type="checkbox" checked={overwrite} onChange={e => setOverwrite(e.target.checked)} /> Overwrite lines that already exist (otherwise only missing lines are added)</label>
    </Modal>
  )
}

function PushDialog({ version, brands, settings, onClose }: { version: VersionSummary; brands: string[]; settings: Settings | null; onClose: () => void }) {
  const [selected, setSelected] = useState<Set<string>>(new Set(brands))
  const [runId, setRunId] = useState<number | null>(null)
  const [run, setRun] = useState<PushRun | null>(null)
  const [expected, setExpected] = useState(0)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    if (runId === null) return
    let stop = false
    const tick = async () => {
      try {
        const r = await api.pushRun(runId)
        if (stop) return
        setRun(r.run)
        if (r.run.status === 'Running') setTimeout(tick, 700)
      } catch (e) { setErr((e as Error).message) }
    }
    tick()
    return () => { stop = true }
  }, [runId])

  const start = async () => {
    setErr(null)
    try {
      const list = [...selected]
      const r = await api.push(version.id, list.length === brands.length ? undefined : list)
      const v = await api.version(version.id)
      const main = settings?.pushMainBudget ? new Set(v.lines.map(l => l.accountCode)).size : 0
      setExpected(v.lines.filter(l => selected.has(l.brandCode)).length + main)
      setRunId(r.runId)
    } catch (e) { setErr((e as Error).message) }
  }

  const pattern = settings?.scenarioNamePattern ?? '{brand} {year}'
  const scen = (b: string) => pattern.replace(/\{brand\}/gi, b).replace(/\{year\}/gi, String(version.fiscalYear))
  const done = run && run.status !== 'Running'
  const failed = run?.items.filter(i => i.action === 'Failed') ?? []
  const count = (a: string) => run?.items.filter(i => i.action === a).length ?? 0

  return (
    <Modal wide={!!run} title="Push to SAP B1" onClose={onClose} footer={
      runId === null ? <>
        <button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={selected.size === 0} onClick={start}>Push {selected.size} brand{selected.size === 1 ? '' : 's'}</button>
      </> : <>
        {done && <button onClick={() => go({ page: 'history', runId: runId })}>Open in history</button>}
        <button className="primary" disabled={!done} onClick={onClose}>{done ? 'Close' : 'Pushing…'}</button>
      </>}>
      {err && <div className="banner error">{err}</div>}
      {settings?.mode === 'Mock' && <div className="banner warn">Mock mode — the push is simulated; nothing is written to SAP B1.</div>}
      {runId === null ? <>
        <span className="muted">
          Each brand (cost center) becomes its own B1 <strong>budget scenario</strong> for FY {version.fiscalYear}, with one budget per G/L account
          (revenue on the credit side, expenses on the debit side).
          {settings?.pushMainBudget && <> <strong>{settings.mainScenarioName}</strong> receives the total of all brands per account.</>}
          {' '}Existing budgets are updated in place, and anything pushed earlier that {revLabel(version)} no longer contains is set to zero — it is safe to push again.
        </span>
        <div className="table-wrap" style={{ maxHeight: 300 }}>
          <table className="data">
            <thead><tr><th style={{ width: 30 }}><input type="checkbox" checked={selected.size === brands.length}
              onChange={e => setSelected(e.target.checked ? new Set(brands) : new Set())} /></th><th>Brand</th><th>B1 budget scenario</th></tr></thead>
            <tbody>
              {brands.sort().map(b => (
                <tr key={b}>
                  <td><input type="checkbox" checked={selected.has(b)} onChange={e => {
                    const s = new Set(selected); if (e.target.checked) s.add(b); else s.delete(b); setSelected(s)
                  }} /></td>
                  <td>{b}</td><td className="code">{scen(b)}</td>
                </tr>
              ))}
              {settings?.pushMainBudget && (
                <tr>
                  <td><input type="checkbox" checked disabled /></td>
                  <td><strong>All brands</strong> <span className="small muted">(always the full revision)</span></td>
                  <td className="code">{settings.mainScenarioName}</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </> : <>
        <div className="row">
          {run ? <PushPill status={run.status} /> : <span className="status">Starting</span>}
          <span className="muted">{run?.items.length ?? 0} of {expected} lines</span>
          <div className="grow" />
          {run && <span className="small">
            <span className="ok">{count('Created')} created</span> · {count('Updated')} updated · <span className="muted">{count('Unchanged')} unchanged</span> · {count('Cleared')} cleared · <span className={failed.length ? 'bad' : 'muted'}>{failed.length} failed</span>
          </span>}
        </div>
        <div className="progress"><i style={{ width: `${expected ? Math.min(100, (run?.items.length ?? 0) / expected * 100) : 0}%` }} /></div>
        {done && run?.message && <div className={`banner ${run.status === 'Succeeded' ? 'success' : run.status === 'Failed' ? 'error' : 'warn'}`}>{run.message}</div>}
        {failed.length > 0 && (
          <div className="table-wrap" style={{ maxHeight: 320 }}>
            <table className="data">
              <thead><tr><th>Brand</th><th>Account</th><th>Scenario</th><th className="num">Annual</th><th>Error from SAP B1</th></tr></thead>
              <tbody>{failed.map(i => (
                <tr key={i.id}><td>{i.brandCode}</td><td className="code">{i.accountCode}</td><td>{i.scenario}</td>
                  <td className="num">{fmt(i.annual)}</td><td className="bad">{i.error}</td></tr>
              ))}</tbody>
            </table>
          </div>
        )}
      </>}
    </Modal>
  )
}

function ReconcileDialog({ versionId, onClose }: { versionId: number; onClose: () => void }) {
  const [rows, setRows] = useState<ReconcileRow[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => { api.reconcile(versionId).then(setRows).catch(e => setErr(e.message)) }, [versionId])
  const mismatches = rows?.filter(r => r.error || !r.match) ?? []
  return (
    <Modal wide title="Compare with SAP B1 budgets" onClose={onClose} footer={<button className="primary" onClick={onClose}>Close</button>}>
      {err && <div className="banner error">{err}</div>}
      {!rows && !err && <div className="empty">Reading budget scenarios from SAP B1…</div>}
      {rows && (mismatches.length === 0
        ? <div className="banner success">All {rows.length} budget lines match what SAP B1 holds.</div>
        : <div className="banner warn">{mismatches.length} of {rows.length} lines differ from SAP B1 (or were never pushed).</div>)}
      {rows && rows.length > 0 && (
        <div className="table-wrap" style={{ maxHeight: 420 }}>
          <table className="data">
            <thead><tr><th>Brand</th><th>Scenario</th><th>Account</th><th className="num">This app</th><th className="num">SAP B1</th><th>Status</th></tr></thead>
            <tbody>{rows.map((r, i) => (
              <tr key={i}>
                <td>{r.brand === '*' ? <strong>All brands</strong> : r.brand}</td><td>{r.scenario}</td><td className="code">{r.account ?? ''}</td>
                <td className="num">{r.app !== undefined ? fmt2(r.app) : ''}</td>
                <td className="num">{r.b1 != null ? fmt2(r.b1) : '—'}</td>
                <td>{r.error ? <span className="bad">{r.error}</span> : r.match ? <span className="status ok">Match</span>
                  : r.b1 == null ? <span className="status neutral">Not in B1</span> : <span className="status warn">Differs</span>}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </Modal>
  )
}

function RenameDialog({ version, onClose, onSaved }: { version: VersionSummary; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(version.name)
  const [notes, setNotes] = useState(version.notes ?? '')
  return (
    <Modal title="Rename budget" onClose={onClose} footer={<>
      <button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={!name.trim()} onClick={async () => { await api.updateVersion(version.id, { name, notes: notes || null }); onSaved() }}>Save</button>
    </>}>
      <label className="field"><span>Name</span><input value={name} onChange={e => setName(e.target.value)} /></label>
      <label className="field"><span>Notes</span><input value={notes} onChange={e => setNotes(e.target.value)} /></label>
    </Modal>
  )
}
