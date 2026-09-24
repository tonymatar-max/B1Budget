import { useState } from 'react'
import { api, type DeptStatus, type DeptView } from './api'
import { Modal } from './ui'

type Action = 'submit' | 'approve' | 'reject' | 'reopen'

const label: Record<Action, string> = { submit: 'Submit for approval', approve: 'Approve', reject: 'Reject', reopen: 'Reopen' }

export function DeptPill({ status }: { status: DeptStatus }) {
  const cls = status === 'Approved' ? 'ok' : status === 'Rejected' ? 'bad' : status === 'Submitted' ? 'warn' : 'neutral'
  const text = status === 'Submitted' ? 'Awaiting approval' : status
  return <span className={`status ${cls}`}>{text}</span>
}

/** The workflow buttons a user may use on one department, each confirmed with an optional (reject: required) comment. */
export function DeptActions({ versionId, dept, onDone, onError, compact, beforeSubmit }: {
  versionId: number
  dept: DeptView
  onDone: (msg: string) => void
  onError: (msg: string) => void
  compact?: boolean
  /** e.g. save unsaved edits first; return false to cancel. */
  beforeSubmit?: () => Promise<boolean>
}) {
  const [action, setAction] = useState<Action | null>(null)
  const [comment, setComment] = useState('')
  const [busy, setBusy] = useState(false)

  const actions: Action[] = [
    ...(dept.canSubmit && dept.status !== 'Submitted' ? ['submit' as const] : []),
    ...(dept.canApprove ? ['approve' as const] : []),
    ...(dept.canReject ? ['reject' as const] : []),
    ...(dept.canReopen ? ['reopen' as const] : []),
  ]
  if (actions.length === 0) return null

  const run = async () => {
    if (!action) return
    if (action === 'reject' && !comment.trim()) { onError('Add a comment saying what needs to change.'); return }
    setBusy(true)
    try {
      if (action === 'submit' && beforeSubmit && !(await beforeSubmit())) { setBusy(false); return }
      await api.deptAction(versionId, dept.brand, action, comment.trim() || undefined)
      setAction(null); setComment('')
      window.dispatchEvent(new Event('workflow-changed'))   // e.g. refresh the approvals badge
      onDone(`${dept.brand}: ${action === 'submit' ? 'submitted for approval' : action === 'approve' ? 'approved' : action === 'reject' ? 'rejected' : 'reopened for changes'}.`)
    } catch (e) { onError((e as Error).message) } finally { setBusy(false) }
  }

  return (
    <>
      <span className="row" style={{ gap: 6, flexWrap: 'nowrap' }}>
        {actions.map(a => (
          <button key={a} className={a === 'approve' || a === 'submit' ? 'primary' : a === 'reject' ? 'danger' : ''}
            style={compact ? { minHeight: 26, padding: '0 9px', fontSize: 12 } : undefined}
            onClick={e => { e.stopPropagation(); setAction(a) }}>
            {compact && a === 'submit' ? 'Submit' : dept.status !== 'Submitted' && a === 'approve' ? 'Approve directly' : label[a]}
          </button>
        ))}
      </span>
      {action && (
        <Modal title={`${label[action]} · ${dept.brand} ${dept.brandName !== dept.brand ? `(${dept.brandName})` : ''}`} onClose={() => setAction(null)} footer={<>
          <button onClick={() => setAction(null)}>Cancel</button>
          <button className={action === 'reject' ? 'danger' : 'primary'} disabled={busy} onClick={run}>{busy ? 'Please wait…' : label[action]}</button>
        </>}>
          <span className="muted">
            {action === 'submit' && <>The budget for {dept.brand} is locked and sent to {dept.approver ?? 'your manager'} for approval.</>}
            {action === 'approve' && (dept.status === 'Submitted'
              ? <>Approve the budget {dept.submittedBy ? `${dept.submittedBy} submitted` : 'submitted'} for {dept.brand}. It then counts in the global view.</>
              : <>Approve {dept.brand} without a submission from its owner (administrator override).</>)}
            {action === 'reject' && <>Send {dept.brand} back to {dept.submittedBy ?? 'the owner'} for changes. Say what needs to change.</>}
            {action === 'reopen' && <>Unlock {dept.brand} so its owner can change it again. It will need to be submitted and approved again.</>}
          </span>
          <label className="field"><span>Comment{action === 'reject' ? ' (required)' : ' (optional)'}</span>
            <textarea value={comment} onChange={e => setComment(e.target.value)} autoFocus style={{ fontFamily: 'inherit', fontSize: 13 }} />
          </label>
        </Modal>
      )}
    </>
  )
}
