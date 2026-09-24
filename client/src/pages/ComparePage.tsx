import { Fragment, useEffect, useMemo, useState } from 'react'
import { api, getCompanyId, setCompanyId, type Account, type Brand, type ChangeKind, type CompareResult, type CompareRow, type VersionSummary } from '../api'
import { go } from '../App'
import { fmt, pct, sum } from '../format'
import { Empty, VersionPill, revLabel } from '../ui'

type Kind = 'Revenue' | 'Expense' | 'Unknown'

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

  // Accounts that aren't in the company's chart of accounts are neither revenue nor expense: they are shown
  // separately and kept out of every total (same rule as the budget list and the server).
  const kind = (acct: string): Kind => {
    const k = accounts.get(acct)?.kind
    return k === 'Revenue' ? 'Revenue' : k === 'Expense' ? 'Expense' : 'Unknown'
  }
  const rows = (result?.rows ?? []).filter(r => (!changedOnly || r.change !== 'Same') && (!brandFilter || r.brand === brandFilter))
  const all = result?.rows ?? []
  const total = (k: Kind, which: 'a' | 'b', rs: CompareRow[] = all) =>
    rs.filter(r => kind(r.account) === k).reduce((s, r) => s + sum(r[which]), 0)
  const counts = (c: ChangeKind) => all.filter(r => r.change === c).length
  const byBrand = [...new Set(rows.map(r => r.brand))]
  const unknownA = total('Unknown', 'a'), unknownB = total('Unknown', 'b')

  const lineRow = (r: CompareRow, k: Kind) => {
    const key = `${r.brand}|${r.account}`
    const d = sum(r.b) - sum(r.a)
    const good = k === 'Revenue' ? d : k === 'Expense' ? -d : 0   // more revenue / less cost is favourable
    return (
      <Fragment key={key}>
        <tr className="clickable" onClick={() => setOpen(open === key ? null : key)}>
          <td style={{ paddingLeft: 28 }}>{open === key ? '▾' : '▸'} <span className="code">{r.account}</span> {accounts.get(r.account)?.name ?? 'not in chart of accounts'}
            <span className="small muted"> · {k === 'Unknown' ? 'unmapped' : k}</span></td>
          <td><span className={`status ${changeCls[r.change]}`}>{r.change}</span></td>
          <td className="num">{fmt(sum(r.a))}</td><td className="num">{fmt(sum(r.b))}</td>
          <td className={`num ${good ? (good > 0 ? 'ok' : 'bad') : 'muted'}`}>{d > 0 ? '+' : ''}{fmt(d)}</td>
          <td className="num muted">{sum(r.a) ? pct(d / sum(r.a) * 100) : '—'}</td>
        </tr>
        {open === key && result && (
          <tr><td colSpan={6} style={{ background: '#fafbfe', padding: '8px 12px 12px 28px' }}>
            <MonthlyDiff a={r.a} b={r.b} good={k === 'Revenue' ? 1 : k === 'Expense' ? -1 : 0}
              labelA={`Rev ${result.a.revisionNo}`} labelB={`Rev ${result.b.revisionNo}`} />
          </td></tr>
        )}
      </Fragment>
    )
  }

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
        {(unknownA !== 0 || unknownB !== 0) && (
          <div className="banner warn">
            Some lines use accounts that aren’t in the chart of accounts ({fmt(unknownA)} in Rev {result.a.revisionNo}, {fmt(unknownB)} in Rev {result.b.revisionNo}).
            They are listed under “Not in chart of accounts” and are not counted in revenue, expenses or net.
          </div>
        )}
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
                    const changedHere = rs.filter(r => r.change !== 'Same').length
                    return (
                      <Fragment key={br}>
                        <tr className="group">
                          <td colSpan={6}>{br}{brands.get(br)?.name ? <span className="muted"> · {brands.get(br)!.name}</span> : <span className="small warn"> · not a cost center in this company</span>}
                            <span className="small muted"> · {changedHere} changed line{changedHere === 1 ? '' : 's'}</span></td>
                        </tr>
                        {(['Revenue', 'Expense', 'Unknown'] as Kind[]).map(k => {
                          const ks = rs.filter(r => kind(r.account) === k)
                          if (ks.length === 0) return null
                          return (
                            <Fragment key={k}>
                              {ks.map(r => lineRow(r, k))}
                              <SubtotalRow label={k === 'Revenue' ? 'Total revenue' : k === 'Expense' ? 'Total expenses' : 'Not in chart of accounts (not counted)'}
                                a={total(k, 'a', ks)} b={total(k, 'b', ks)} good={k === 'Revenue' ? 1 : k === 'Expense' ? -1 : 0} />
                            </Fragment>
                          )
                        })}
                        {rs.some(r => kind(r.account) !== 'Unknown') && (
                          <SubtotalRow label="Net contribution (revenue − expenses)" strong
                            a={total('Revenue', 'a', rs) - total('Expense', 'a', rs)} b={total('Revenue', 'b', rs) - total('Expense', 'b', rs)} good={1} />
                        )}
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

/** Subtotal of the lines shown above it; the difference is coloured by whether it is good (+revenue, −cost). */
function SubtotalRow({ label, a, b, good, strong }: { label: string; a: number; b: number; good: 1 | -1 | 0; strong?: boolean }) {
  const d = b - a
  const fav = d * good
  return (
    <tr className={strong ? 'total' : 'subtotal'}>
      <td style={{ paddingLeft: 28 }}>{label}</td><td />
      <td className="num">{fmt(a)}</td><td className="num">{fmt(b)}</td>
      <td className={`num ${fav > 0 ? 'ok' : fav < 0 ? 'bad' : ''}`}>{d > 0 ? '+' : ''}{fmt(d)}</td>
      <td className="num">{a ? pct(d / Math.abs(a) * 100) : '—'}</td>
    </tr>
  )
}

function MonthlyDiff({ a, b, labelA, labelB, good }: { a: number[]; b: number[]; labelA: string; labelB: string; good: 1 | -1 | 0 }) {
  return (
    <table className="data" style={{ width: 'auto' }}>
      <thead><tr><th /> {a.map((_, i) => <th key={i} className="num">P{i + 1}</th>)}</tr></thead>
      <tbody>
        <tr><td className="muted nowrap">{labelA}</td>{a.map((x, i) => <td key={i} className="num">{fmt(x)}</td>)}</tr>
        <tr><td className="muted nowrap">{labelB}</td>{b.map((x, i) => <td key={i} className="num">{fmt(x)}</td>)}</tr>
        <tr><td className="muted">Δ</td>{b.map((x, i) => {
          const d = x - a[i]
          return <td key={i} className={`num ${d * good > 0 ? 'ok' : d * good < 0 ? 'bad' : 'muted'}`}>{d ? (d > 0 ? '+' : '') + fmt(d) : '·'}</td>
        })}</tr>
      </tbody>
    </table>
  )
}
