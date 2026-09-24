import { useEffect, useState } from 'react'
import { api, type VersionSummary } from '../api'
import { go } from '../App'
import { useAuth } from '../auth'
import { fmt, when } from '../format'
import { Empty, Modal, VersionPill, revLabel, useToast } from '../ui'

export default function BudgetsPage() {
  const { isAdmin } = useAuth()
  const [versions, setVersions] = useState<VersionSummary[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  const [, setToast, toastNode] = useToast()

  const load = () => api.versions().then(setVersions).catch(e => setToast({ kind: 'error', text: e.message }))
  useEffect(() => { load() }, [])

  const remove = async (v: VersionSummary) => {
    if (!confirm(`Delete draft "${revLabel(v)}" (${v.fiscalYear})? This cannot be undone.`)) return
    try { await api.deleteVersion(v.id); load() } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Budgets</h1>
          <span className="muted">{isAdmin
            ? 'Budget revisions by cost center × G/L account × month. Department owners enter and submit their part; approve the budget, then push it to SAP B1.'
            : 'Open the current budget to enter your department’s figures, then submit them to your manager for approval.'}</span>
        </div>
        <label className="check"><input type="checkbox" checked={showHistory} onChange={e => setShowHistory(e.target.checked)} /> Show superseded revisions</label>
        {isAdmin && <button className="primary" onClick={() => setCreating(true)}>New budget</button>}
      </div>

      <div className="panel flush">
        <div className="panel-body table-wrap">
          {versions === null ? <div className="empty">Loading…</div> : versions.length === 0 ? (
            <Empty>
              <strong>No budgets yet</strong>
              <span>Create a budget version for a fiscal year, then enter it by brand or seed it from last year’s actuals.</span>
              {isAdmin && <button className="primary" onClick={() => setCreating(true)}>New budget</button>}
            </Empty>
          ) : (
            <table className="data">
              <thead>
                <tr>
                  <th>Year</th><th>Name</th><th>Revision</th><th>Status</th><th className="num">Brands</th><th className="num">Lines</th>
                  <th className="num">Revenue budget</th><th className="num">Expense budget</th><th className="num">Net</th>
                  <th>Last pushed</th><th></th>
                </tr>
              </thead>
              <tbody>
                {versions.filter(v => showHistory || v.status !== 'Superseded').map(v => (
                  <tr key={v.id} className="clickable" onClick={() => go({ page: 'editor', id: v.id })}
                    style={v.status === 'Superseded' ? { opacity: .6 } : undefined}>
                    <td><strong>{v.fiscalYear}</strong></td>
                    <td>{v.name}{v.notes && <div className="small muted">{v.notes}</div>}</td>
                    <td className="nowrap">Rev {v.revisionNo}{versions.filter(x => x.familyId === v.familyId).length > 1 &&
                      <span className="small muted"> of {Math.max(...versions.filter(x => x.familyId === v.familyId).map(x => x.revisionNo))}</span>}</td>
                    <td><VersionPill status={v.status} /></td>
                    <td className="num">{v.brandCount}</td>
                    <td className="num">{v.lineCount}</td>
                    <td className="num">{fmt(v.revenue)}</td>
                    <td className="num">{fmt(v.expense)}</td>
                    <td className={`num ${v.revenue - v.expense < 0 ? 'bad' : ''}`}>{fmt(v.revenue - v.expense)}</td>
                    <td className="nowrap muted">{when(v.lastPushedAt)}</td>
                    <td className="nowrap" onClick={e => e.stopPropagation()}>
                      <button className="link" onClick={() => go({ page: 'bva', id: v.id })}>vs actual</button>
                      {v.parentVersionId && <>{' · '}<button className="link" onClick={() => go({ page: 'compare', a: v.parentVersionId!, b: v.id })}>changes</button></>}
                      {isAdmin && v.status === 'Draft' && <>{' · '}<button className="link" style={{ color: 'var(--danger)' }} onClick={() => remove(v)}>Delete</button></>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {creating && <CreateDialog versions={versions ?? []} onClose={() => setCreating(false)}
        onCreated={id => go({ page: 'editor', id })} onError={t => setToast({ kind: 'error', text: t })} />}
      {toastNode}
    </div>
  )
}

function CreateDialog({ versions, onClose, onCreated, onError }: {
  versions: VersionSummary[]; onClose: () => void; onCreated: (id: number) => void; onError: (t: string) => void
}) {
  const nextYear = new Date().getFullYear() + (new Date().getMonth() >= 8 ? 1 : 0)
  const [year, setYear] = useState(nextYear)
  const [name, setName] = useState(`Original ${nextYear}`)
  const [notes, setNotes] = useState('')
  const [copyFrom, setCopyFrom] = useState<number | ''>('')
  const [busy, setBusy] = useState(false)

  const submit = async () => {
    setBusy(true)
    try {
      const r = await api.createVersion({ fiscalYear: year, name, notes: notes || undefined, copyFromVersionId: copyFrom === '' ? null : copyFrom })
      onCreated(r.id)
    } catch (e) { onError((e as Error).message); setBusy(false) }
  }

  return (
    <Modal title="New budget" onClose={onClose} footer={<>
      <button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={busy || !name.trim()} onClick={submit}>Create</button>
    </>}>
      <div className="form-grid">
        <label className="field"><span>Fiscal year</span>
          <input type="number" value={year} onChange={e => setYear(Number(e.target.value))} />
        </label>
        <label className="field"><span>Name</span>
          <input value={name} onChange={e => setName(e.target.value)} placeholder="Original, Reforecast Q2…" />
        </label>
      </div>
      <label className="field"><span>Copy lines from (optional)</span>
        <select value={copyFrom} onChange={e => setCopyFrom(e.target.value === '' ? '' : Number(e.target.value))}>
          <option value="">— Start empty —</option>
          {versions.map(v => <option key={v.id} value={v.id}>{v.fiscalYear} · {revLabel(v)}</option>)}
        </select>
      </label>
      <label className="field"><span>Notes</span>
        <input value={notes} onChange={e => setNotes(e.target.value)} />
      </label>
      <span className="small muted">Tip: after creating, use “Seed from actuals” to start from last year’s figures plus a growth %.</span>
    </Modal>
  )
}
