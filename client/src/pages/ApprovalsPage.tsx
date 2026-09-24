import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, getCompanyId, setCompanyId, type DeptView, type InboxItem, type VersionSummary } from '../api'
import { go } from '../App'
import { useAuth } from '../auth'
import { DeptActions, DeptPill } from '../DeptActions'
import { fmt, when } from '../format'
import { Empty, VersionPill, revLabel, useToast } from '../ui'

/**
 * Admins: the global view — every cost center of a budget revision with its owner, amounts and approval state,
 * plus the final approval of the whole revision. Managers/users: the same for their departments, and an inbox.
 */
export default function ApprovalsPage({ versionId }: { versionId?: number }) {
  const { isAdmin, isApprover } = useAuth()
  const [inbox, setInbox] = useState<InboxItem[] | null>(null)
  const [versions, setVersions] = useState<VersionSummary[] | null>(null)
  const [depts, setDepts] = useState<DeptView[] | null>(null)
  const [filter, setFilter] = useState<'all' | 'Draft' | 'Submitted' | 'Approved' | 'Rejected'>('all')
  const [, setToast, toastNode] = useToast()

  useEffect(() => { api.versions().then(setVersions).catch(e => setToast({ kind: 'error', text: e.message })) }, [])

  // Default: the newest draft (the one being worked on), else the newest revision.
  const vid = versionId ?? versions?.find(v => v.status === 'Draft')?.id ?? versions?.find(v => v.status !== 'Superseded')?.id ?? versions?.[0]?.id
  const version = versions?.find(v => v.id === vid) ?? null

  const load = useCallback(() => {
    if (isApprover) api.inbox().then(setInbox).catch(() => setInbox([]))
    if (vid) api.departments(vid).then(setDepts).catch(e => setToast({ kind: 'error', text: e.message }))
  }, [vid, isApprover])
  useEffect(load, [load])

  const done = (msg: string) => { setToast({ kind: 'success', text: msg }); load(); api.versions().then(setVersions) }
  const fail = (msg: string) => setToast({ kind: 'error', text: msg })

  const counts = useMemo(() => {
    const c = { Draft: 0, Submitted: 0, Approved: 0, Rejected: 0 }
    for (const d of depts ?? []) c[d.status]++
    return c
  }, [depts])
  const withLines = (depts ?? []).filter(d => d.lineCount > 0)
  const approvedNet = withLines.filter(d => d.status === 'Approved').reduce((s, d) => s + d.revenue - d.expense, 0)
  const totalNet = withLines.reduce((s, d) => s + d.revenue - d.expense, 0)
  const pending = withLines.filter(d => d.status !== 'Approved')
  const shown = (depts ?? []).filter(d => filter === 'all' || d.status === filter)

  const approveAll = async () => {
    if (!version || !confirm(`Approve ${revLabel(version)} as the company budget? It will be locked and ready to push to SAP B1.`)) return
    try { await api.approve(version.id); done(`${revLabel(version)} approved — push it to SAP B1 from the budget page.`) } catch (e) { fail((e as Error).message) }
  }

  const openInbox = (i: InboxItem) => {
    if (i.companyId !== getCompanyId()) setCompanyId(i.companyId)
    go({ page: 'editor', id: i.versionId, brand: i.brand })
  }

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>{isAdmin ? 'Global view' : 'Approvals'}</h1>
          <span className="muted">{isAdmin
            ? 'Every cost center of the budget, who owns it and where it is in the approval flow. Once all are approved, approve the budget and push it to SAP B1.'
            : 'Your departments’ budgets and, if you manage a team, the submissions waiting for you.'}</span>
        </div>
        {versions && versions.length > 0 && (
          <select value={vid ?? ''} onChange={e => go({ page: 'approvals', v: Number(e.target.value) })}>
            {versions.map(v => <option key={v.id} value={v.id}>{v.fiscalYear} · {revLabel(v)} · {v.status === 'Pushed' ? 'In SAP B1' : v.status}</option>)}
          </select>
        )}
      </div>

      {isApprover && (
        <div className="panel flush">
          <div className="panel-head">
            <h2>Waiting for {isAdmin ? 'approval' : 'your approval'}</h2>
            {inbox && <span className={`status ${inbox.length ? 'warn' : 'neutral'}`}>{inbox.length}</span>}
          </div>
          <div className="panel-body table-wrap">
            {!inbox ? <div className="empty">Loading…</div> : inbox.length === 0 ? <div className="empty">Nothing is waiting — all caught up.</div> : (
              <table className="data">
                <thead><tr><th>Cost center</th><th>Budget</th><th>Company</th><th>Submitted by</th><th>When</th><th>Note</th><th /></tr></thead>
                <tbody>{inbox.map(i => (
                  <tr key={`${i.versionId}-${i.brand}`} className="clickable" onClick={() => openInbox(i)}>
                    <td><strong>{i.brand}</strong>{isAdmin && !i.assignedToMe && <span className="small muted"> · for another approver</span>}</td>
                    <td>{i.fiscalYear} · {i.budget}</td><td>{i.companyName}</td><td>{i.submittedBy}</td>
                    <td className="nowrap muted">{when(i.submittedAt)}</td><td className="muted">{i.comment}</td>
                    <td><button className="primary" style={{ minHeight: 26 }}>Review</button></td>
                  </tr>
                ))}</tbody>
              </table>
            )}
          </div>
        </div>
      )}

      {versions && versions.length === 0 ? (
        <div className="panel"><Empty><strong>No budgets yet</strong>
          <span>{isAdmin ? 'Create a budget for the fiscal year; each department owner then enters their part.' : 'Your administrator hasn’t opened a budget yet.'}</span>
        </Empty></div>
      ) : version && depts && <>
        <div className="tiles">
          <div className="tile"><div className="label">Approved</div><div className="value ok">{counts.Approved}</div><div className="sub">of {depts.length} cost centers</div></div>
          <div className="tile"><div className="label">Awaiting approval</div><div className="value warn">{counts.Submitted}</div><div className="sub">submitted by owners</div></div>
          <div className="tile"><div className="label">Being prepared</div><div className="value">{counts.Draft + counts.Rejected}</div>
            <div className="sub">{counts.Rejected ? <span className="bad">{counts.Rejected} rejected · </span> : null}{counts.Draft} draft</div></div>
          <div className="tile"><div className="label">Approved net contribution</div><div className="value">{fmt(approvedNet)}</div>
            <div className="sub">of {fmt(totalNet)} across all entered budgets</div></div>
        </div>

        {isAdmin && version.status === 'Draft' && (
          <div className={`banner ${pending.length ? 'info' : 'success'} row`}>
            <span className="grow">{pending.length
              ? <>{pending.length} cost center{pending.length > 1 ? 's' : ''} with budget lines still need approval before {revLabel(version)} can be approved as the company budget.</>
              : <>Every department is approved. Approve {revLabel(version)} to lock it, then push it to SAP B1.</>}</span>
            <button className="primary" disabled={pending.length > 0} onClick={approveAll}>Approve budget</button>
          </div>
        )}
        {version.status !== 'Draft' && (
          <div className="banner info">{revLabel(version)} is <VersionPill status={version.status} /> — departments are locked. Changes go through a new revision.</div>
        )}

        <div className="panel flush">
          <div className="panel-head">
            <h2>{isAdmin ? 'All cost centers' : 'Departments'} · {revLabel(version)}</h2>
            <div className="grow" />
            <div className="seg">
              {(['all', 'Draft', 'Submitted', 'Approved', 'Rejected'] as const).map(f => (
                <button key={f} className={filter === f ? 'on' : ''} onClick={() => setFilter(f)}>
                  {f === 'all' ? `All (${depts.length})` : `${f === 'Submitted' ? 'Awaiting' : f} (${counts[f]})`}
                </button>
              ))}
            </div>
          </div>
          <div className="panel-body table-wrap">
            {shown.length === 0 ? <div className="empty">No cost centers in this view.</div> : (
              <table className="data">
                <thead><tr><th>Cost center</th><th>Owner</th><th>Status</th><th className="num">Revenue</th><th className="num">Expenses</th>
                  <th className="num">Net</th><th>Submitted</th><th>Decision</th><th /></tr></thead>
                <tbody>{shown.map(d => (
                  <tr key={d.brand} className="clickable" onClick={() => go({ page: 'editor', id: version.id, brand: d.brand })}>
                    <td><strong>{d.brand}</strong> <span className="muted">· {d.brandName}</span></td>
                    <td>{d.owners.length ? d.owners.join(', ') : <span className="muted small">no owner</span>}</td>
                    <td><DeptPill status={d.status} /></td>
                    <td className="num">{d.lineCount ? fmt(d.revenue) : <span className="muted">—</span>}</td>
                    <td className="num">{d.lineCount ? fmt(d.expense) : <span className="muted">—</span>}</td>
                    <td className={`num ${d.revenue - d.expense < 0 ? 'bad' : ''}`}>{d.lineCount ? fmt(d.revenue - d.expense) : ''}</td>
                    <td className="small">{d.submittedBy ? <>{d.submittedBy}<div className="muted">{when(d.submittedAt)}</div></> : <span className="muted">—</span>}</td>
                    <td className="small">{d.decidedBy
                      ? <>{d.status === 'Rejected' ? 'Rejected' : 'Approved'} by {d.decidedBy}{d.comment && <div className="muted">“{d.comment}”</div>}</>
                      : d.status === 'Submitted' ? <span className="muted">with {d.approver}</span> : <span className="muted">—</span>}</td>
                    <td onClick={e => e.stopPropagation()}><DeptActions compact versionId={version.id} dept={d} onDone={done} onError={fail} /></td>
                  </tr>
                ))}</tbody>
              </table>
            )}
          </div>
        </div>
      </>}
      {toastNode}
    </div>
  )
}
