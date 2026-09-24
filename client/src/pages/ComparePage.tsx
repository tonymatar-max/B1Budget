import { Fragment, useEffect, useMemo, useState } from 'react'
import { api, getCompanyId, setCompanyId, type Account, type Brand, type ChangeKind, type CompareResult, type CompareRow, type VersionSummary } from '../api'
import { go } from '../App'
import { fmt, pct, sum } from '../format'
import { Empty, VersionPill, revLabel } from '../ui'

const changeCls: Record<ChangeKind, string> = { Added: 'ok', Removed: 'bad', Changed: 'warn', Same: 'neutral' }

export default function ComparePage({ a, b }: { a?: number; b?: number }) {
  const [versions, setVersions] = useState<VersionSummary[] | null>(null)
  const [accounts, setAccounts] = useState<Map<string, Account>>(new Map())
  const [brands, setBrands] = useState<Map<string, Brand>>(new Map())
  const [result, setResult] = useState<CompareResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [changedOnly, setChangedOnly] = useState(true)
  const [brandFilter, setBrandFilter] = useState('')
  const [open, setOpen] = useState<string | null>(null)

  useEffect(() => {
    const ref = b ?? a
    if (ref) api.version(ref).then(v => { if (v.version.companyId !== getCompanyId()) setCompanyId(v.version.companyId) }).catch(() => {})
    Promise.all([api.versions(), api.accounts(), api.brands()]).then(([v, acc, br]) => {
      setVersions(v)
      setAccounts(new Map(acc.map(x => [x.code, x])))
      setBrands(new Map(br.map(x => [x.code, x])))
    }).catch(e => setError(e.message))
  }, [])

  // Sensible defaults: B = given (or the newest revision that has a parent), A = its parent revision.
  const pick = useMemo(() => {
    if (!versions) return null
    let vb = b ? versions.find(v => v.id === b) : undefined
    if (!vb && !a) vb = versions.find(v => v.parentVersionId) ?? versions[0]
    const va = a ? versions.find(v => v.id === a) : versions.find(v => v.id === vb?.parentVersionId) ?? versions.find(v => v.id !== vb?.id)
    return { a: va?.id, b: vb?.id }
  }, [versions, a, b])

  useEffect(() => {
    if (!pick?.a || !pick?.b) return
    setResult(null)
    api.compare(pick.a, pick.b).then(setResult).catch(e => setError(e.message))
  }, [pick?.a, pick?.b])

  if (versions && versions.length < 2)
    return <div className="page"><h1>Compare revisions</h1><div className="panel"><Empty>
      <strong>Nothing to compare yet</strong>
      <span>Approve a budget, then use “Create revision” to make Rev 2. Every revision is kept, so you can compare any two.</span>
      <button className="primary" onClick={() => go({ page: 'budgets' })}>Go to budgets</button>
    </Empty></div></div>

  const kind = (acct: string) => accounts.get(acct)?.kind === 'Revenue' ? 'Revenue' : 'Expense'
  const signed = (r: CompareRow, which: 'a' | 'b') => (kind(r.account) === 'Revenue' ? 1 : -1) * sum(r[which])
  const rows = (result?.rows ?? []).filter(r => (!changedOnly || r.change !== 'Same') && (!brandFilter || r.brand === brandFilter))
  const all = result?.rows ?? []
  const total = (k: 'Revenue' | 'Expense', which: 'a' | 'b') => all.filter(r => kind(r.account) === k).reduce((s, r) => s + sum(r[which]), 0)
  const counts = (c: ChangeKind) => all.filter(r => r.change === c).length
  const byBrand = [...new Set(rows.map(r => r.brand))]

  const option = (v: VersionSummary) => <option key={v.id} value={v.id}>{v.fiscalYear} · {revLabel(v)} · {v.status === 'Pushed' ? 'In SAP B1' : v.status}</option>

  const exportCsv = () => {
    if (!result) return
    const head = ['Brand', 'Account', 'Account name', 'Change', `Annual ${revLabel(result.a)}`, `Annual ${revLabel(result.b)}`, 'Difference',
      ...Array.from({ length: 12 }, (_, i) => [`P${i + 1} A`, `P${i + 1} B`]).flat()]
    const lines = rows.map(r => [r.brand, r.account, accounts.get(r.account)?.name ?? '', r.change, sum(r.a), sum(r.b), sum(r.b) - sum(r.a),
      ...r.a.flatMap((x, i) => [x, r.b[i]])])
    const csv = [head, ...lines].map(l => l.map(c => `"${String(c).replace(/"/g, '""')}"`).join(',')).join('\r\n')
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }))
    const el = document.createElement('a'); el.href = url; el.download = `BudgetChanges_Rev${result.a.revisionNo}_Rev${result.b.revisionNo}.csv`; el.click()
    URL.revokeObjectURL(url)
  }

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Compare revisions</h1>
          <span className="muted">What changed between two budget revisions, by brand, account and month.</span>
        </div>
        <div className="row">
          <select value={pick?.a ?? ''} onChange={e => go({ page: 'compare', a: Number(e.target.value), b: pick?.b })} title="From (base)">
            {versions?.map(option)}
          </select>
          <span className="muted">→</span>
          <select value={pick?.b ?? ''} onChange={e => go({ page: 'compare', a: pick?.a, b: Number(e.target.value) })} title="To">
            {versions?.map(option)}
          </select>
          <button disabled={!result} onClick={exportCsv}>Export CSV</button>
        </div>
      </div>

      {error && <div className="banner error">{error}</div>}
      {!result && !error && <div className="empty">Loading…</div>}
      {result && <>
        {result.a.fiscalYear !== result.b.fiscalYear && <div className="banner info">Comparing different fiscal years ({result.a.fiscalYear} vs {result.b.fiscalYear}) — periods are matched by position (P1 with P1).</div>}
        <div className="tiles">
          <DeltaTile label="Revenue" a={total('Revenue', 'a')} b={total('Revenue', 'b')} good={1} />
          <DeltaTile label="Expenses" a={total('Expense', 'a')} b={total('Expense', 'b')} good={-1} />
          <DeltaTile label="Net contribution" a={total('Revenue', 'a') - total('Expense', 'a')} b={total('Revenue', 'b') - total('Expense', 'b')} good={1} />
          <div className="tile">
            <div className="label">Lines</div>
            <div className="value">{counts('Changed') + counts('Added') + counts('Removed')} <span className="small muted">changed</span></div>
            <div className="sub"><span className="warn">{counts('Changed')} changed</span> · <span className="ok">{counts('Added')} added</span> · <span className="bad">{counts('Removed')} removed</span> · {counts('Same')} same</div>
          </div>
        </div>

        <div className="panel flush">
          <div className="panel-head">
            <div className="row">
              <strong>{revLabel(result.a)}</strong> <VersionPill status={result.a.status} />
              <span className="muted">→</span>
              <strong>{revLabel(result.b)}</strong> <VersionPill status={result.b.status} />
            </div>
            <div className="grow" />
            <select value={brandFilter} onChange={e => setBrandFilter(e.target.value)}>
              <option value="">All brands</option>
              {[...new Set(all.map(r => r.brand))].sort().map(c => <option key={c} value={c}>{c} · {brands.get(c)?.name ?? ''}</option>)}
            </select>
            <label className="check"><input type="checkbox" checked={changedOnly} onChange={e => setChangedOnly(e.target.checked)} /> Changed lines only</label>
          </div>
          <div className="panel-body table-wrap">
            {rows.length === 0 ? <div className="empty">{changedOnly ? 'No differences between these revisions.' : 'No lines.'}</div> : (
              <table className="data">
                <thead>
                  <tr><th>Brand / account</th><th>Change</th><th className="num">{`Rev ${result.a.revisionNo}`}</th><th className="num">{`Rev ${result.b.revisionNo}`}</th>
                    <th className="num">Difference</th><th className="num">%</th></tr>
                </thead>
                <tbody>
                  {byBrand.map(br => {
                    const rs = rows.filter(r => r.brand === br)
                    const na = rs.reduce((s, r) => s + signed(r, 'a'), 0), nb = rs.reduce((s, r) => s + signed(r, 'b'), 0)
                    return (
                      <Fragment key={br}>
                        <tr className="group">
                          <td>{br} <span className="muted">· {brands.get(br)?.name ?? ''}</span> <span className="small muted">(net of shown lines)</span></td>
                          <td /><td className="num">{fmt(na)}</td><td className="num">{fmt(nb)}</td>
                          <td className={`num ${nb - na > 0 ? 'ok' : nb - na < 0 ? 'bad' : ''}`}>{nb - na > 0 ? '+' : ''}{fmt(nb - na)}</td>
                          <td className="num">{na ? pct((nb - na) / Math.abs(na) * 100) : '—'}</td>
                        </tr>
                        {rs.map(r => {
                          const key = `${r.brand}|${r.account}`
                          const d = sum(r.b) - sum(r.a)
                          const good = kind(r.account) === 'Revenue' ? d : -d   // more revenue / less cost is favourable
                          return (
                            <Fragment key={key}>
                              <tr className="clickable" onClick={() => setOpen(open === key ? null : key)}>
                                <td style={{ paddingLeft: 28 }}>{open === key ? '▾' : '▸'} <span className="code">{r.account}</span> {accounts.get(r.account)?.name ?? '(unknown account)'}
                                  <span className="small muted"> · {kind(r.account)}</span></td>
                                <td><span className={`status ${changeCls[r.change]}`}>{r.change}</span></td>
                                <td className="num">{fmt(sum(r.a))}</td><td className="num">{fmt(sum(r.b))}</td>
                                <td className={`num ${good ? (good > 0 ? 'ok' : 'bad') : 'muted'}`}>{d > 0 ? '+' : ''}{fmt(d)}</td>
                                <td className="num muted">{sum(r.a) ? pct(d / sum(r.a) * 100) : '—'}</td>
                              </tr>
                              {open === key && (
                                <tr><td colSpan={6} style={{ background: '#fafbfe', padding: '8px 12px 12px 28px' }}>
                                  <MonthlyDiff a={r.a} b={r.b} labelA={`Rev ${result.a.revisionNo}`} labelB={`Rev ${result.b.revisionNo}`} />
                                </td></tr>
                              )}
                            </Fragment>
                          )
                        })}
                      </Fragment>
                    )
                  })}
                </tbody>
              </table>
            )}
          </div>
        </div>
      </>}
    </div>
  )
}

function DeltaTile({ label, a, b, good }: { label: string; a: number; b: number; good: 1 | -1 }) {
  const d = b - a
  const fav = d * good > 0
  return (
    <div className="tile">
      <div className="label">{label}</div>
      <div className="value">{fmt(b)}</div>
      <div className="sub">was {fmt(a)} · <span className={d === 0 ? '' : fav ? 'ok' : 'bad'}>{d > 0 ? '+' : ''}{fmt(d)} ({a ? pct(d / Math.abs(a) * 100) : '—'})</span></div>
    </div>
  )
}

function MonthlyDiff({ a, b, labelA, labelB }: { a: number[]; b: number[]; labelA: string; labelB: string }) {
  return (
    <table className="data" style={{ width: 'auto' }}>
      <thead><tr><th /> {a.map((_, i) => <th key={i} className="num">P{i + 1}</th>)}</tr></thead>
      <tbody>
        <tr><td className="muted nowrap">{labelA}</td>{a.map((x, i) => <td key={i} className="num">{fmt(x)}</td>)}</tr>
        <tr><td className="muted nowrap">{labelB}</td>{b.map((x, i) => <td key={i} className="num">{fmt(x)}</td>)}</tr>
        <tr><td className="muted">Δ</td>{b.map((x, i) => {
          const d = x - a[i]
          return <td key={i} className={`num ${d > 0 ? 'ok' : d < 0 ? 'bad' : 'muted'}`}>{d ? (d > 0 ? '+' : '') + fmt(d) : '·'}</td>
        })}</tr>
      </tbody>
    </table>
  )
}
