import { Fragment, useEffect, useMemo, useState } from 'react'
import { api, type GroupReport, type GroupRow } from '../api'
import { fmt, pct, sum, when } from '../format'

/** Favourable variance is positive: more revenue, less cost. */
const variance = (budget: number, actual: number, kind: 'Revenue' | 'Expense' | 'net') =>
  kind === 'Expense' ? budget - actual : actual - budget

type Metric = 'revenue' | 'expense' | 'net'
const sign = (r: GroupRow, m: Metric) =>
  r.kind === 'Other' ? 0   // not in the chart of accounts: excluded from every total
    : m === 'net' ? (r.kind === 'Revenue' ? 1 : -1) : (m === 'revenue') === (r.kind === 'Revenue') ? 1 : 0

function totals(rows: GroupRow[], m: Metric, f: number, t: number, companyId?: number) {
  let budget = 0, actual = 0
  for (const r of rows) {
    const s = sign(r, m)
    if (!s) continue
    const src = companyId === undefined ? r : r.byCompany.find(p => p.companyId === companyId)
    if (!src) continue
    budget += s * sum(src.budget, f - 1, t)
    actual += s * sum(src.actual, f - 1, t)
  }
  return { budget, actual }
}

export default function GroupPage() {
  const [year, setYear] = useState(new Date().getFullYear())
  const [from, setFrom] = useState<number | undefined>()
  const [to, setTo] = useState<number | undefined>()
  const [picks, setPicks] = useState<Record<number, number>>({})
  const [report, setReport] = useState<GroupReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [expanded, setExpanded] = useState<string | null>(null)
  const [asOf] = useState(() => new Date().toISOString())

  const load = async (refresh = false) => {
    setLoading(true); setError(null)
    try {
      const r = await api.group(year, from, to, refresh, picks)
      setReport(r)
      if (from === undefined) setFrom(r.fromPeriod)
      if (to === undefined) setTo(r.toPeriod)
    } catch (e) { setError((e as Error).message) } finally { setLoading(false) }
  }
  useEffect(() => { load() }, [year, from, to, picks])

  const f = report?.fromPeriod ?? 1
  const t = report?.toPeriod ?? 12
  const rows = report?.rows ?? []
  const companyName = useMemo(() => new Map((report?.companies ?? []).map(c => [c.id, c.name])), [report])
  const brands = useMemo(() => {
    const m = new Map<string, { code: string; name: string; rows: GroupRow[] }>()
    for (const r of rows) {
      if (!m.has(r.brand)) m.set(r.brand, { code: r.brand, name: r.brandName, rows: [] })
      m.get(r.brand)!.rows.push(r)
    }
    return [...m.values()]
  }, [rows])

  const exportCsv = () => {
    if (!report) return
    const cos = report.companies
    const head = ['Brand', 'Brand name', 'Account', 'Account name', 'Type', 'Budget', 'Actual', 'Variance',
      ...cos.flatMap(c => [`${c.name} budget`, `${c.name} actual`])]
    const lines = rows.map(r => {
      const b = sum(r.budget, f - 1, t), a = sum(r.actual, f - 1, t)
      return [r.brand, r.brandName, r.account, r.accountName, r.kind, b, a, variance(b, a, r.kind === 'Revenue' ? 'Revenue' : 'Expense'),
        ...cos.flatMap(c => { const p = r.byCompany.find(x => x.companyId === c.id); return p ? [sum(p.budget, f - 1, t), sum(p.actual, f - 1, t)] : [0, 0] })]
    })
    const csv = [head, ...lines].map(l => l.map(c => `"${String(c).replace(/"/g, '""')}"`).join(',')).join('\r\n')
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }))
    const el = document.createElement('a'); el.href = url; el.download = `GroupBudgetVsActual_${report.fiscalYear}_P${f}-${t}.csv`; el.click()
    URL.revokeObjectURL(url)
  }

  const rev = totals(rows, 'revenue', f, t), exp = totals(rows, 'expense', f, t), net = totals(rows, 'net', f, t)

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Group budget vs actual</h1>
          <span className="muted small">All companies in group currency (local amount × each company’s group rate), summed by brand code and account code. Read {when(asOf)}.</span>
        </div>
        <div className="row">
          <label className="row small muted" style={{ gap: 6 }}>Fiscal year
            <input type="number" value={year} style={{ width: 90 }} onChange={e => { setFrom(undefined); setTo(undefined); setPicks({}); setYear(Number(e.target.value)) }} />
          </label>
          {report && <>
            <select value={f} onChange={e => setFrom(Number(e.target.value))} title="From period">
              {report.periodLabels.map((p, i) => <option key={i} value={i + 1}>{p}</option>)}
            </select>
            <span className="muted">to</span>
            <select value={t} onChange={e => setTo(Number(e.target.value))} title="To period">
              {report.periodLabels.map((p, i) => <option key={i} value={i + 1} disabled={i + 1 < f}>{p}</option>)}
            </select>
          </>}
          <button disabled={loading} onClick={() => load(true)}>{loading ? 'Loading…' : 'Refresh actuals'}</button>
          <button disabled={!report} onClick={exportCsv}>Export CSV</button>
        </div>
      </div>

      {error && <div className="banner error">{error}</div>}
      {!report && !error && <div className="empty">Reading every company…</div>}

      {report && <>
        <div className="panel flush">
          <div className="panel-head"><h2>Companies</h2><span className="small muted">Pick which approved revision each company contributes.</span></div>
          <div className="panel-body table-wrap">
            <table className="data">
              <thead><tr><th>Company</th><th>Currency</th><th className="num">Rate</th><th>Budget revision</th>
                <th className="num">Net budget</th><th className="num">Net actual</th><th className="num">Variance</th></tr></thead>
              <tbody>
                {report.companies.map(c => {
                  const n = totals(rows, 'net', f, t, c.id)
                  const v = variance(n.budget, n.actual, 'net')
                  return (
                    <tr key={c.id}>
                      <td><strong>{c.name}</strong>{c.error && <div className="small bad">{c.error}</div>}</td>
                      <td>{c.currency || '—'}</td>
                      <td className="num">{c.groupRate}</td>
                      <td>{c.options.length === 0 ? <span className="muted small">No approved budget for {report.fiscalYear}
                          {c.otherYears.length > 0 && <> — has {c.otherYears.map((y, i) => <span key={y}>{i ? ', ' : ''}
                            <button className="link small" onClick={() => { setFrom(undefined); setTo(undefined); setPicks({}); setYear(y) }}>FY {y}</button></span>)}</>}</span> : (
                        <select value={c.versionId ?? ''} onChange={e => setPicks({ ...picks, [c.id]: Number(e.target.value) })}>
                          {c.options.map(o => <option key={o.id} value={o.id}>{o.label}</option>)}
                        </select>)}</td>
                      <td className="num">{fmt(n.budget)}</td>
                      <td className="num">{c.error ? '—' : fmt(n.actual)}</td>
                      <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{c.error ? '' : `${v > 0 ? '+' : ''}${fmt(v)}`}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>
        {report.companies.some(c => c.error) && <div className="banner warn">Some companies could not be read — the totals below exclude their actuals.</div>}

        <div className="tiles">
          {([['Revenue', rev, 'Revenue'], ['Expenses', exp, 'Expense'], ['Net contribution', net, 'net']] as const).map(([label, x, k]) => {
            const v = variance(x.budget, x.actual, k)
            return (
              <div className="tile" key={label}>
                <div className="label">{label}</div>
                <div className="value">{fmt(x.actual)}</div>
                <div className="sub">vs budget {fmt(x.budget)} · <span className={v >= 0 ? 'ok' : 'bad'}>{v >= 0 ? '+' : ''}{fmt(v)} ({x.budget ? pct(x.actual / x.budget * 100) : '—'})</span></div>
              </div>
            )
          })}
        </div>

        <div className="panel flush">
          <div className="panel-head">
            <h2>By brand and account</h2>
            <span className="muted small">{report.periodLabels[f - 1]} – {report.periodLabels[t - 1]} · click an account for the split by company</span>
            <div className="grow" />
            <button className="link small" onClick={() => setOpen(new Set(brands.map(b => b.code)))}>Expand all</button>
            <button className="link small" onClick={() => setOpen(new Set())}>Collapse all</button>
          </div>
          <div className="panel-body table-wrap">
            {brands.length === 0 ? <div className="empty">No budget or actuals for {report.fiscalYear}.</div> : (
              <table className="data">
                <thead><tr><th>Brand / account</th><th className="num">Budget</th><th className="num">Actual</th><th className="num">Variance</th><th className="num">Var %</th><th>Companies</th></tr></thead>
                <tbody>
                  {brands.map(b => {
                    const n = totals(b.rows, 'net', f, t)
                    const v = variance(n.budget, n.actual, 'net')
                    const isOpen = open.has(b.code)
                    const cos = new Set(b.rows.flatMap(r => r.byCompany.map(p => p.companyId)))
                    return (
                      <Fragment key={b.code}>
                        <tr className="group clickable" onClick={() => { const s = new Set(open); if (isOpen) s.delete(b.code); else s.add(b.code); setOpen(s) }}>
                          <td>{isOpen ? '▾' : '▸'} {b.code || '—'} <span className="muted">· {b.name}</span> <span className="small muted">(net)</span></td>
                          <td className="num">{fmt(n.budget)}</td><td className="num">{fmt(n.actual)}</td>
                          <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{v > 0 ? '+' : ''}{fmt(v)}</td>
                          <td className="num">{n.budget ? pct(v / Math.abs(n.budget) * 100) : '—'}</td>
                          <td className="small muted">{[...cos].map(id => companyName.get(id)).join(', ')}</td>
                        </tr>
                        {isOpen && b.rows.map(r => {
                          const key = `${r.brand}|${r.account}`
                          const k = r.kind === 'Revenue' ? 'Revenue' : 'Expense'
                          const bb = sum(r.budget, f - 1, t), aa = sum(r.actual, f - 1, t), vv = variance(bb, aa, k)
                          return (
                            <Fragment key={key}>
                              <tr className="clickable" onClick={() => setExpanded(expanded === key ? null : key)}>
                                <td style={{ paddingLeft: 28 }}><span className="code">{r.account}</span> {r.accountName} <span className="small muted">· {k}</span></td>
                                <td className="num">{fmt(bb)}</td><td className="num">{fmt(aa)}</td>
                                <td className={`num ${vv > 0.5 ? 'ok' : vv < -0.5 ? 'bad' : ''}`}>{vv > 0 ? '+' : ''}{fmt(vv)}</td>
                                <td className="num">{bb ? pct(vv / Math.abs(bb) * 100) : '—'}</td>
                                <td className="small muted">{r.byCompany.length} {r.byCompany.length === 1 ? 'company' : 'companies'}</td>
                              </tr>
                              {expanded === key && r.byCompany.map(p => {
                                const pb = sum(p.budget, f - 1, t), pa = sum(p.actual, f - 1, t), pv = variance(pb, pa, k)
                                return (
                                  <tr key={key + p.companyId} style={{ background: '#fafbfe' }}>
                                    <td style={{ paddingLeft: 52 }} className="muted">{companyName.get(p.companyId)}</td>
                                    <td className="num muted">{fmt(pb)}</td><td className="num muted">{fmt(pa)}</td>
                                    <td className={`num ${pv > 0.5 ? 'ok' : pv < -0.5 ? 'bad' : 'muted'}`}>{pv > 0 ? '+' : ''}{fmt(pv)}</td>
                                    <td className="num muted">{pb ? pct(pv / Math.abs(pb) * 100) : '—'}</td><td />
                                  </tr>
                                )
                              })}
                            </Fragment>
                          )
                        })}
                      </Fragment>
                    )
                  })}
                  <tr className="total">
                    <td>Group net contribution</td><td className="num">{fmt(net.budget)}</td><td className="num">{fmt(net.actual)}</td>
                    <td className="num">{(() => { const v = variance(net.budget, net.actual, 'net'); return `${v > 0 ? '+' : ''}${fmt(v)}` })()}</td>
                    <td className="num">{net.budget ? pct(variance(net.budget, net.actual, 'net') / Math.abs(net.budget) * 100) : '—'}</td><td />
                  </tr>
                </tbody>
              </table>
            )}
          </div>
        </div>
      </>}
    </div>
  )
}
