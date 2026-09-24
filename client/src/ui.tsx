import { useEffect, useState, type ReactNode } from 'react'
import type { PushStatus, VersionStatus } from './api'

export function Modal({ title, onClose, children, footer, wide }: {
  title: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode; wide?: boolean
}) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', k)
    return () => window.removeEventListener('keydown', k)
  }, [onClose])
  return (
    <div className="overlay" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <div className={`modal${wide ? ' wide' : ''}`} role="dialog" aria-modal="true">
        <div className="modal-head">
          <h2>{title}</h2>
          <button className="icon" onClick={onClose} aria-label="Close">✕</button>
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  )
}

export function VersionPill({ status }: { status: VersionStatus }) {
  const cls = status === 'Pushed' ? 'ok' : status === 'Approved' ? '' : status === 'Superseded' ? 'warn' : 'neutral'
  const label = status === 'Pushed' ? 'In SAP B1' : status
  return <span className={`status ${cls}`}>{label}</span>
}

export function PushPill({ status }: { status: PushStatus }) {
  const cls = status === 'Succeeded' ? 'ok' : status === 'Failed' ? 'bad' : status === 'PartiallyFailed' ? 'warn' : ''
  const label = status === 'PartiallyFailed' ? 'Partial' : status
  return <span className={`status ${cls}`}>{label}</span>
}

/** "Original 2026 · Rev 2" */
export const revLabel = (v: { name: string; revisionNo: number }) => `${v.name} · Rev ${v.revisionNo}`

export type Toast = { kind: 'success' | 'error' | 'info'; text: string } | null

export function useToast(): [Toast, (t: Toast) => void, ReactNode] {
  const [toast, setToast] = useState<Toast>(null)
  useEffect(() => {
    if (!toast) return
    const t = setTimeout(() => setToast(null), toast.kind === 'error' ? 8000 : 4000)
    return () => clearTimeout(t)
  }, [toast])
  const node = toast ? <div className={`banner ${toast.kind} toast`} onClick={() => setToast(null)}>{toast.text}</div> : null
  return [toast, setToast, node]
}

export function Empty({ children }: { children: ReactNode }) {
  return (
    <div className="empty">
      <div className="brand-mark large"><span className="brand-node" /></div>
      {children}
    </div>
  )
}
