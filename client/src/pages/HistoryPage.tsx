import { useEffect, useState } from 'react'
import { api, type PushAction, type PushRun, type PushRunSummary, type VersionSummary } from '../api'
import { go } from '../App'
import { fmt, when } from '../format'
import { Empty, PushPill } from '../ui'

export default function HistoryPage({ runId }: { runId?: number }) {
  const [runs, setRuns] = useState<PushRunSummary[] | null>(null)
  const [versions, setVersions] = useState<Map<number, VersionSummary>>(new Map())
  const [run, setRun] = useState<PushRun | null>(null)
  const [filter, setFilter] = useState<PushAction | ''>('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    Promise.all([api.pushRuns(), api.versions()])
      .then(([r, v]) => { setRuns(r); setVersions(new Map(v.map(x => [x.id, x]))) })
      .catch(e => setError(e.message))
  }, [])
  useEffect(() => {
    setRun(null)
    if (runId) api.pushRun(runId).then(r => setRun(r.run)).catch(e => setError(e.message))
  }, [runId])

  const items = run?.items.filter(i => !filter || i.action === filter) ?? []

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Push history</h1>
          <span className="muted">Every push to SAP B1 native budgets, with the per-line result returned by Service Layer.</span>
        </div>
      </div>
      {error && <div className="banner error">{error}</div>}

      <div className="panel flush">
        <div className="panel-body table-wrap" style={{ maxHeight: runId ? 260 : undefined }}>
          {runs === null ? <div className="empty">Loading…</div> : runs.length === 0 ? (
            <Empty><strong>Nothing pushed yet</strong><span>Approve a budget and use “Push to SAP B1”.</span></Empty>
          ) : (
            <table className="data">
              <thead><tr><th>#</th><th>Budget</th><th>Started</th><th>Mode</th><th>Status</th><th className="num">Lines</th><th className="num">Failed</th><th>Result</th></tr></thead>
              <tbody>
                {runs.map(r => {
                  const v = versions.get(r.versionId)
                  return (
                    <tr key={r.id} className="clickable" onClick={() => go({ page: 'history', runId: r.id })}
                      style={r.id === runId ? { background: 'var(--accent-soft)' } : undefined}>
                      <td className="code">{r.id}</td>
                      <td>{v ? `${v.fiscalYear} · ${v.name} · Rev ${v.revisionNo}` : `Version ${r.versionId} (deleted)`}</td>
                      <td className="nowrap">{when(r.startedAt)}</td>
                      <td>{r.mode === 'Mock' ? <span className="status warn">Mock</span> : <span className="status neutral">SAP B1</span>}</td>
                      <td><PushPill status={r.status} /></td>
                      <td className="num">{r.total}</td>
                      <td className={`num ${r.failed ? 'bad' : 'muted'}`}>{r.failed}</td>
                      <td className="muted">{r.message}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {run && (
        <div className="panel flush">
          <div className="panel-head">
            <h2>Run #{run.id}</h2><PushPill status={run.status} />
            <span className="muted small">{when(run.startedAt)} → {when(run.finishedAt)}</span>
            <div className="grow" />
            <div className="seg">
              {(['', 'Created', 'Updated', 'Unchanged', 'Cleared', 'Failed'] as const).map(a => (
                <button key={a} className={filter === a ? 'on' : ''} onClick={() => setFilter(a)}>
                  {a || 'All'} ({a ? run.items.filter(i => i.action === a).length : run.items.length})
                </button>
              ))}
            </div>
          </div>
          <div className="panel-body table-wrap" style={{ maxHeight: 'calc(100vh - 420px)', minHeight: 200 }}>
            <table className="data">
              <thead><tr><th>Brand</th><th>Scenario</th><th>Account</th><th className="num">Annual</th><th>Result</th><th>B1 key</th><th>Error</th></tr></thead>
              <tbody>
                {items.map(i => (
                  <tr key={i.id}>
                    <td>{i.brandCode === '*' ? <strong>All brands</strong> : i.brandCode}</td><td>{i.scenario}</td><td className="code">{i.accountCode}</td>
                    <td className="num">{fmt(i.annual)}</td>
                    <td><span className={`status ${i.action === 'Failed' ? 'bad' : i.action === 'Unchanged' ? 'neutral' : i.action === 'Cleared' ? 'warn' : 'ok'}`}>{i.action}</span></td>
                    <td className="code">{i.b1Key}</td>
                    <td className="bad">{i.error}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  )
}
