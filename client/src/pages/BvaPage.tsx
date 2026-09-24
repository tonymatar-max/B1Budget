import { useEffect, useMemo, useState } from 'react'
import { api, getCompanyId, setCompanyId, type BvaReport, type BvaRow, type JournalLine, type VersionSummary } from '../api'
import { go } from '../App'
import { day, fmt, fmt2, pct, short, sum, when } from '../format'
import { Empty, Modal, revLabel } from '../ui'

type Metric = 'net' | 'revenue' | 'expense'

interface Agg { budget: number; actual: number; fyBudget: number; compare: number; monthlyBudget: number[]; monthlyActual: number[]; monthlyCompare: number[] }

const sign = (r: BvaRow, m: Metric) =>
  r.kind === 'Other' ? 0   // not in the chart of accounts: excluded from every total
    : m === 'net' ? (r.kind === 'Revenue' ? 1 : -1) : (m === 'revenue') === (r.kind === 'Revenue') ? 1 : 0

function aggregate(rows: BvaRow[], m: Metric, from: number, to: number): Agg {
  const a: Agg = { budget: 0, actual: 0, fyBudget: 0, compare: 0, monthlyBudget: Array(12).fill(0), monthlyActual: Array(12).fill(0), monthlyCompare: Array(12).fill(0) }
  for (const r of rows) {
    const s = sign(r, m)
    if (!s) continue
    a.budget += s * sum(r.budget, from - 1, to)
    a.actual += s * sum(r.actual, from - 1, to)
    a.fyBudget += s * sum(r.budget)
    if (r.compare) a.compare += s * sum(r.compare, from - 1, to)
    for (let i = 0; i < 12; i++) {
      a.monthlyBudget[i] += s * r.budget[i]; a.monthlyActual[i] += s * r.actual[i]
      if (r.compare) a.monthlyCompare[i] += s * r.compare[i]
    }
  }
  return a
}

/** Favourable variance is positive: more revenue, less cost. */
const variance = (budget: number, actual: number, kind: 'Revenue' | 'Expense' | 'net') =>
  kind === 'Expense' ? budget - actual : actual - budget

export default function BvaPage({ versionId }: { versionId?: number }) {
  const [versions, setVersions] = useState<VersionSummary[] | null>(null)
  const [report, setReport] = useState<BvaReport | null>(null)
  const [from, setFrom] = useState<number | undefined>()
  const [to, setTo] = useState<number | undefined>()
  const [brandFilter, setBrandFilter] = useState('')
  const [metric, setMetric] = useState<Metric>('net')
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [drill, setDrill] = useState<BvaRow | null>(null)
  const [compareId, setCompareId] = useState<number | ''>('')

  useEffect(() => {
    // Opening another company's budget by link switches the active company to it.
    if (versionId) api.version(versionId).then(v => { if (v.version.companyId !== getCompanyId()) setCompanyId(v.version.companyId) }).catch(() => {})
    api.versions().then(setVersions).catch(e => setError(e.message))
  }, [])
  const vid = versionId ?? versions?.[0]?.id

  const load = async (refresh = false) => {
    if (!vid) return
    setLoading(true); setError(null)
    try {
      const r = await api.bva(vid, from, to, refresh, compareId === '' ? undefined : compareId)
      setReport(r)
      if (from === undefined) setFrom(r.fromPeriod)
      if (to === undefined) setTo(r.toPeriod)
    } catch (e) { setError((e as Error).message) } finally { setLoading(false) }
  }
  useEffect(() => { load() }, [vid, from, to, compareId])

  const rows = useMemo(() => (report?.rows ?? []).filter(r => !brandFilter || r.brand === brandFilter), [report, brandFilter])
  const brands = useMemo(() => {
    const m = new Map<string, { code: string; name: string; rows: BvaRow[] }>()
    for (const r of report?.rows ?? []) {
      if (!m.has(r.brand)) m.set(r.brand, { code: r.brand, name: r.brandName, rows: [] })
      m.get(r.brand)!.rows.push(r)
    }
    return [...m.values()]
  }, [report])

  if (versions && versions.length === 0)
    return <div className="page"><h1>Budget vs actual</h1><div className="panel"><Empty>
      <strong>No budgets yet</strong><span>Create a budget first.</span>
      <button className="primary" onClick={() => go({ page: 'budgets' })}>Go to budgets</button>
    </Empty></div></div>

  const f = report?.fromPeriod ?? 1
  const t = report?.toPeriod ?? 12
  const rev = aggregate(rows, 'revenue', f, t)
  const exp = aggregate(rows, 'expense', f, t)
  const net = aggregate(rows, 'net', f, t)
  const chartAgg = metric === 'net' ? net : metric === 'revenue' ? rev : exp
  const cmpName = report?.compareVersionName ?? null
  const cmpShort = cmpName ? cmpName.split(' · ').pop()! : ''
  const unassigned = (report?.rows ?? []).filter(r => r.brand === '')
  const unassignedAmt = unassigned.reduce((s, r) => s + sum(r.actual, f - 1, t), 0)
  const visibleBrands = brands.filter(b => !brandFilter || b.code === brandFilter)

  const exportCsv = () => {
    if (!report) return
    const head = ['Brand', 'Brand name', 'Account', 'Account name', 'Type', 'Budget', 'Actual', 'Variance', 'Variance %', 'FY budget',
      ...report.periodLabels.flatMap(p => [`${p} budget`, `${p} actual`])]
    const lines = rows.map(r => {
      const b = sum(r.budget, f - 1, t), a = sum(r.actual, f - 1, t), v = variance(b, a, r.kind === 'Revenue' ? 'Revenue' : 'Expense')
      return [r.brand, r.brandName, r.account, r.accountName, r.kind, b, a, v, b ? (v / b * 100).toFixed(1) : '', sum(r.budget),
        ...r.budget.flatMap((x, i) => [x, r.actual[i]])]
    })
    const csv = [head, ...lines].map(l => l.map(c => `"${String(c).replace(/"/g, '""')}"`).join(',')).join('\r\n')
    const url = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' }))
    const a = document.createElement('a'); a.href = url; a.download = `BudgetVsActual_${report.fiscalYear}_P${f}-${t}.csv`; a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Budget vs actual</h1>
          <span className="muted small">
            Actuals are posted journal lines (JDT1) by cost center and G/L account, excluding period-end closing.
            {report && <> Read {when(report.actualsAsOf)}.</>}
          </span>
        </div>
        <div className="row">
          <select value={vid ?? ''} onChange={e => { setFrom(undefined); setTo(undefined); setCompareId(''); go({ page: 'bva', id: Number(e.target.value) }) }}>
            {versions?.map(v => <option key={v.id} value={v.id}>{v.fiscalYear} · {revLabel(v)}{v.status === 'Superseded' ? ' (superseded)' : ''}</option>)}
          </select>
          <select value={compareId} onChange={e => setCompareId(e.target.value === '' ? '' : Number(e.target.value))} title="Second budget to compare against">
            <option value="">No second budget</option>
            {versions?.filter(v => v.id !== vid).map(v => <option key={v.id} value={v.id}>vs {v.fiscalYear} · {revLabel(v)}</option>)}
          </select>
          {report && <>
            <select value={f} onChange={e => setFrom(Number(e.target.value))} title="From period">
              {report.periodLabels.map((p, i) => <option key={i} value={i + 1}>{p}</option>)}
            </select>
            <span className="muted">to</span>
            <select value={t} onChange={e => setTo(Number(e.target.value))} title="To period">
              {report.periodLabels.map((p, i) => <option key={i} value={i + 1} disabled={i + 1 < f}>{p}</option>)}
            </select>
            <select value={brandFilter} onChange={e => setBrandFilter(e.target.value)}>
              <option value="">All brands</option>
              {brands.map(b => <option key={b.code} value={b.code}>{b.code ? `${b.code} · ${b.name}` : b.name}</option>)}
            </select>
          </>}
          <button disabled={loading} onClick={() => load(true)}>{loading ? 'Loading…' : 'Refresh actuals'}</button>
          <button disabled={!report} onClick={exportCsv}>Export CSV</button>
        </div>
      </div>

      {error && <div className="banner error">{error}</div>}
      {!report && !error && <div className="empty">Loading actuals from SAP B1…</div>}

      {report && <>
        <div className="tiles">
          <KpiTile label="Revenue" budget={rev.budget} actual={rev.actual} fy={rev.fyBudget} kind="Revenue" cmp={cmpName ? rev.compare : null} cmpName={cmpName} />
          <KpiTile label="Expenses" budget={exp.budget} actual={exp.actual} fy={exp.fyBudget} kind="Expense" cmp={cmpName ? exp.compare : null} cmpName={cmpName} />
          <KpiTile label="Net contribution" budget={net.budget} actual={net.actual} fy={net.fyBudget} kind="net" cmp={cmpName ? net.compare : null} cmpName={cmpName} />
          <div className="tile">
            <div className="label">Postings without a brand</div>
            <div className={`value ${unassignedAmt ? 'warn' : ''}`}>{fmt(unassignedAmt)}</div>
            <div className="sub">{unassignedAmt ? `${unassigned.length} P&L account(s) posted with no dimension code — not in any brand's actuals.` : 'Every P&L posting in the period carries a brand.'}</div>
          </div>
        </div>

        <div className="panel">
          <div className="panel-head">
            <h2>Monthly {metric === 'net' ? 'net contribution' : metric}{brandFilter && ` · ${brandFilter}`}</h2>
            <div className="grow" />
            <div className="legend"><span><i style={{ background: '#c9d1f5' }} />Budget</span><span><i style={{ background: 'var(--accent)' }} />Actual</span><span><i style={{ background: 'var(--danger)' }} />Actual, unfavourable</span>{cmpName && <span><i style={{ background: 'var(--warn)', height: 2, verticalAlign: 3 }} />{cmpShort} budget</span>}</div>
            <div className="seg">
              {(['net', 'revenue', 'expense'] as Metric[]).map(m =>
                <button key={m} className={metric === m ? 'on' : ''} onClick={() => setMetric(m)}>{m === 'net' ? 'Net' : m === 'revenue' ? 'Revenue' : 'Expenses'}</button>)}
            </div>
          </div>
          <div className="panel-body">
            <MonthlyChart labels={report.periodLabels} budget={chartAgg.monthlyBudget} actual={chartAgg.monthlyActual}
              from={f} to={t} current={report.currentPeriod} expense={metric === 'expense'} compare={cmpName ? chartAgg.monthlyCompare : null} />
          </div>
        </div>

        <div className="panel flush">
          <div className="panel-head">
            <h2>By brand and account</h2>
            <span className="muted small">{report.periodLabels[f - 1]} – {report.periodLabels[t - 1]} · click an account to see its journal lines</span>
            <div className="grow" />
            <button className="link small" onClick={() => setOpen(new Set(visibleBrands.map(b => b.code)))}>Expand all</button>
            <button className="link small" onClick={() => setOpen(new Set())}>Collapse all</button>
          </div>
          <div className="panel-body table-wrap">
            {visibleBrands.length === 0 ? <div className="empty">No budget or actuals in this period.</div> : (
              <table className="data">
                <thead>
                  <tr><th>Brand / account</th><th className="num">Budget</th><th className="num">Actual</th><th className="num">Variance</th>
                    <th className="num">Var %</th><th style={{ width: 150 }}>Used of budget</th><th className="num">FY budget</th>
                    {cmpName && <><th className="num">{cmpShort} budget</th><th className="num">Variance vs {cmpShort}</th></>}</tr>
                </thead>
                <tbody>
                  {visibleBrands.map(b => {
                    const a = aggregate(b.rows, 'net', f, t)
                    const isOpen = open.has(b.code)
                    const toggle = () => { const s = new Set(open); if (isOpen) s.delete(b.code); else s.add(b.code); setOpen(s) }
                    return [
                      <tr key={b.code} className="group clickable" onClick={toggle}>
                        <td>{isOpen ? '▾' : '▸'} {b.code || '—'} <span className="muted">· {b.name}</span> <span className="small muted">(net)</span></td>
                        <VarCells budget={a.budget} actual={a.actual} fy={a.fyBudget} kind="net" cmp={cmpName ? a.compare : null} />
                      </tr>,
                      ...(isOpen ? (['Revenue', 'Expense', 'Other'] as const).flatMap(k => {
                        const rs = b.rows.filter(r => r.kind === k)
                        if (!rs.length) return []
                        // Accounts not in the chart of accounts: listed for visibility, never totalled.
                        if (k === 'Other') return [
                          <tr key={b.code + 'other'} className="subtotal"><td colSpan={cmpName ? 9 : 7} style={{ paddingLeft: 28 }}>
                            Not in chart of accounts (not counted): {rs.map(r => `${r.account} ${fmt(sum(r.budget, f - 1, t))}`).join(' · ')}</td></tr>,
                        ]
                        const s = aggregate(rs, k === 'Revenue' ? 'revenue' : 'expense', f, t)
                        return [
                          ...rs.map(r => (
                            <tr key={b.code + r.account} className="clickable" onClick={() => setDrill(r)}>
                              <td style={{ paddingLeft: 28 }}><span className="code">{r.account}</span> {r.accountName}</td>
                              <VarCells budget={sum(r.budget, f - 1, t)} actual={sum(r.actual, f - 1, t)} fy={sum(r.budget)} kind={k} cmp={r.compare ? sum(r.compare, f - 1, t) : null} />
                            </tr>
                          )),
                          <tr key={b.code + k + 'sub'} className="subtotal">
                            <td style={{ paddingLeft: 28 }}>Total {k === 'Revenue' ? 'revenue' : 'expenses'}</td>
                            <VarCells budget={s.budget} actual={s.actual} fy={s.fyBudget} kind={k} cmp={cmpName ? s.compare : null} />
                          </tr>,
                        ]
                      }) : []),
                    ]
                  })}
                  <tr className="total">
                    <td>Total net contribution</td>
                    <VarCells budget={net.budget} actual={net.actual} fy={net.fyBudget} kind="net" cmp={cmpName ? net.compare : null} />
                  </tr>
                </tbody>
              </table>
            )}
          </div>
        </div>
      </>}

      {drill && report && <DrillDialog versionId={report.versionId} row={drill} from={f} to={t} labels={report.periodLabels} onClose={() => setDrill(null)} />}
    </div>
  )
}

function KpiTile({ label, budget, actual, fy, kind, cmp, cmpName }: {
  label: string; budget: number; actual: number; fy: number; kind: 'Revenue' | 'Expense' | 'net'; cmp?: number | null; cmpName?: string | null
}) {
  const v = variance(budget, actual, kind)
  const used = budget ? actual / budget * 100 : null
  const fyUsed = fy ? Math.max(0, actual / fy * 100) : 0
  const periodMark = fy ? Math.max(0, budget / fy * 100) : 0
  const over = kind === 'Expense' ? actual > budget : actual < budget
  return (
    <div className="tile">
      <div className="label">{label}</div>
      <div className="value">{fmt(actual)}</div>
      <div className="sub">vs budget {fmt(budget)} · <span className={v >= 0 ? 'ok' : 'bad'}>{v >= 0 ? '+' : ''}{fmt(v)} ({pct(used)})</span></div>
      <div className="meter" title={`${fyUsed.toFixed(1)}% of full-year budget ${fmt(fy)}; marker = budget to date`}>
        <i className={over ? 'over' : ''} style={{ width: `${Math.min(100, fyUsed)}%` }} />
        <b style={{ left: `${Math.min(100, periodMark)}%` }} />
      </div>
      <div className="sub">{fyUsed.toFixed(0)}% of FY budget {short(fy)}</div>
      {cmp != null && (() => {
        const vc = variance(cmp, actual, kind)
        return <div className="sub">vs {cmpName?.split(' · ').pop()} {fmt(cmp)} · <span className={vc >= 0 ? 'ok' : 'bad'}>{vc >= 0 ? '+' : ''}{fmt(vc)}</span></div>
      })()}
    </div>
  )
}

function VarCells({ budget, actual, fy, kind, cmp }: { budget: number; actual: number; fy: number; kind: 'Revenue' | 'Expense' | 'net'; cmp?: number | null }) {
  const vc = cmp == null ? 0 : variance(cmp, actual, kind)
  const v = variance(budget, actual, kind)
  const used = budget ? actual / budget * 100 : null
  const over = kind === 'Expense' ? actual > budget : false
  return <>
    <td className="num">{fmt(budget)}</td>
    <td className="num">{fmt(actual)}</td>
    <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{v > 0 ? '+' : ''}{fmt(v)}</td>
    <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{budget ? pct(v / Math.abs(budget) * 100) : '—'}</td>
    <td>{kind !== 'net' && used !== null && budget > 0 && actual >= 0
      ? <div className="row" style={{ gap: 6, flexWrap: 'nowrap' }}>
          <div className="meter" style={{ marginTop: 0, flex: 1 }}><i className={over ? 'over' : ''} style={{ width: `${Math.min(100, used)}%` }} /></div>
          <span className="small muted" style={{ minWidth: 38, textAlign: 'right' }}>{used.toFixed(0)}%</span>
        </div>
      : <span className="muted small">{budget === 0 && actual !== 0 ? 'Unbudgeted' : ''}</span>}</td>
    <td className="num muted">{fmt(fy)}</td>
    {cmp != null && <>
      <td className="num">{fmt(cmp)}</td>
      <td className={`num ${vc > 0.5 ? 'ok' : vc < -0.5 ? 'bad' : ''}`}>{vc > 0 ? '+' : ''}{fmt(vc)}</td>
    </>}
  </>
}

function MonthlyChart({ labels, budget, actual, from, to, current, expense, compare }: {
  labels: string[]; budget: number[]; actual: number[]; from: number; to: number; current: number; expense: boolean; compare: number[] | null
}) {
  const W = 960, H = 240, L = 56, B = 26, T = 10
  const cmp = compare ?? []
  const max = Math.max(1, ...budget.map(Math.abs), ...actual.map(Math.abs), ...cmp.map(Math.abs))
  const min = Math.min(0, ...budget, ...actual, ...cmp)
  const range = max - min || 1
  const y = (v: number) => T + (H - T - B) * (1 - (v - min) / range)
  const slot = (W - L) / 12
  const bw = Math.min(22, slot * 0.32)
  const ticks = 4
  return (
    <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Monthly budget versus actual">
      {Array.from({ length: ticks + 1 }, (_, i) => {
        const v = min + range * i / ticks
        return <g key={i}>
          <line className="gridline" x1={L} x2={W} y1={y(v)} y2={y(v)} />
          <text className="axis" x={L - 8} y={y(v) + 4} textAnchor="end">{short(v)}</text>
        </g>
      })}
      {labels.map((lab, i) => {
        const x = L + slot * i + slot / 2
        const inRange = i + 1 >= from && i + 1 <= to
        const future = current > 0 && i + 1 > current
        const unfav = expense ? actual[i] > budget[i] : actual[i] < budget[i]
        const bar = (v: number, cx: number, cls: string) =>
          <rect className={cls} x={cx} width={bw} y={Math.min(y(v), y(0))} height={Math.max(1, Math.abs(y(v) - y(0)))} rx={2} />
        return (
          <g key={i} opacity={inRange ? 1 : 0.35}>
            <title>{`${lab}\nBudget ${fmt(budget[i])}\nActual ${fmt(actual[i])}`}</title>
            {bar(budget[i], x - bw - 1, 'bar-budget')}
            {future && actual[i] === 0
              ? <rect className="bar-future" x={x + 1} width={bw} y={y(0) - 2} height={2} />
              : bar(actual[i], x + 1, `bar-actual${unfav ? ' over' : ''}`)}
            <text className="axis" x={x} y={H - 8} textAnchor="middle">{lab}</text>
          </g>
        )
      })}
      <line x1={L} x2={W} y1={y(0)} y2={y(0)} stroke="var(--line-strong)" />
      {compare && (
        <g>
          {/* Second budget: a short tick across each month's pair of bars, joined by a line */}
          <polyline fill="none" stroke="var(--warn)" strokeWidth={1.5} strokeDasharray="4 3"
            points={compare.map((v, i) => `${L + slot * i + slot / 2},${y(v)}`).join(' ')} />
          {compare.map((v, i) => {
            const x = L + slot * i + slot / 2
            return <line key={i} x1={x - bw - 3} x2={x + bw + 3} y1={y(v)} y2={y(v)} stroke="var(--warn)" strokeWidth={2.5}>
              <title>{`${labels[i]}: second budget ${fmt(v)}`}</title></line>
          })}
        </g>
      )}
    </svg>
  )
}

function DrillDialog({ versionId, row, from, to, labels, onClose }: {
  versionId: number; row: BvaRow; from: number; to: number; labels: string[]; onClose: () => void
}) {
  const [lines, setLines] = useState<JournalLine[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => { api.drill(versionId, row.brand, row.account, from, to).then(setLines).catch(e => setErr(e.message)) }, [versionId, row, from, to])
  const debit = lines?.reduce((s, l) => s + l.debit, 0) ?? 0
  const credit = lines?.reduce((s, l) => s + l.credit, 0) ?? 0
  return (
    <Modal wide title={<>{row.account} {row.accountName} <span className="muted small">· {row.brand || '(no brand)'} · {labels[from - 1]} – {labels[to - 1]}</span></>}
      onClose={onClose} footer={<button className="primary" onClick={onClose}>Close</button>}>
      <div className="tiles">
        <div className="tile"><div className="label">Budget</div><div className="value">{fmt(sum(row.budget, from - 1, to))}</div></div>
        <div className="tile"><div className="label">Actual</div><div className="value">{fmt(sum(row.actual, from - 1, to))}</div></div>
        <div className="tile"><div className="label">Journal lines</div><div className="value">{lines?.length ?? '…'}</div></div>
      </div>
      {err && <div className="banner error">{err}</div>}
      {!lines && !err && <div className="empty">Loading journal lines…</div>}
      {lines && (
        <div className="table-wrap" style={{ maxHeight: 420 }}>
          <table className="data">
            <thead><tr><th>Date</th><th>JE #</th><th>Type</th><th>Ref.</th><th>Memo</th><th className="num">Debit</th><th className="num">Credit</th></tr></thead>
            <tbody>
              {lines.map(l => (
                <tr key={`${l.transId}-${l.lineId}`}>
                  <td className="nowrap">{day(l.date)}</td><td className="code">{l.transId}</td><td className="muted">{l.transType}</td>
                  <td>{l.ref1}</td><td>{l.memo}</td>
                  <td className="num">{l.debit ? fmt2(l.debit) : ''}</td><td className="num">{l.credit ? fmt2(l.credit) : ''}</td>
                </tr>
              ))}
              <tr className="total"><td colSpan={5}>Total</td><td className="num">{fmt2(debit)}</td><td className="num">{fmt2(credit)}</td></tr>
            </tbody>
          </table>
        </div>
      )}
    </Modal>
  )
}
