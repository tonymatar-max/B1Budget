import { useEffect, useMemo, useState } from 'react'
import { api, type ForecastReport } from '../api'
import { fmt, parseAmount, pct, short, sum, when } from '../format'
import { Empty, useToast } from '../ui'

/** Favourable sales variance is positive: forecasting/selling more than budget is good. */
const vsBudget = (forecast: number, budget: number) => forecast - budget

export default function ForecastPage({ year }: { year?: number }) {
  const [report, setReport] = useState<ForecastReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draft, setDraft] = useState<Record<string, number[]>>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [editing, setEditing] = useState<Record<string, string>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [seeding, setSeeding] = useState(false)
  const [, setToast, toastNode] = useToast()

  const load = async (y?: number, refresh = false) => {
    setLoading(true); setError(null)
    try {
      const r = await api.forecast(y ?? year, refresh)
      setReport(r)
      setDraft(Object.fromEntries(r.rows.map(row => [row.brand, [...row.forecast]])))
      setDirty(new Set()); setEditing({})
    } catch (e) { setError((e as Error).message) } finally { setLoading(false) }
  }
  useEffect(() => { load() }, [year])

  const fy = report?.fiscalYear
  const forecastOf = (b: string) => draft[b] ?? report?.rows.find(r => r.brand === b)?.forecast ?? Array(12).fill(0)

  const setCell = (brand: string, i: number, raw: string) => {
    setEditing(e => ({ ...e, [`${brand}:${i}`]: raw }))
    const n = parseAmount(raw)
    if (n === null) return
    setDraft(d => {
      const next = [...(d[brand] ?? forecastOf(brand))]
      next[i] = n
      return { ...d, [brand]: next }
    })
    setDirty(s => new Set(s).add(brand))
  }
  const focusCell = (brand: string, i: number) => setEditing(e => {
    const v = forecastOf(brand)[i]
    return { ...e, [`${brand}:${i}`]: v ? String(v) : '' }
  })
  const commitCell = (brand: string, i: number) => setEditing(e => { const n = { ...e }; delete n[`${brand}:${i}`]; return n })
  // Formatted (with thousands separators) when idle; the raw number while the cell is being edited.
  const cellValue = (brand: string, i: number) => {
    const k = `${brand}:${i}`
    if (k in editing) return editing[k]
    const v = forecastOf(brand)[i]
    return v ? fmt(v) : ''
  }

  const save = async () => {
    if (!fy || dirty.size === 0) return
    setSaving(true)
    try {
      for (const brand of dirty) await api.saveForecastLine(fy, brand, forecastOf(brand))
      setToast({ kind: 'success', text: `Saved ${dirty.size} cost center${dirty.size > 1 ? 's' : ''}.` })
      await load(fy)
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) } finally { setSaving(false) }
  }

  const seed = async () => {
    if (!fy) return
    if (dirty.size && !confirm('Seeding overwrites your cost centers with actuals (elapsed months) + budget (remaining). Unsaved edits will be lost. Continue?')) return
    setSeeding(true)
    try {
      const r = await api.seedForecast(fy, true)
      setToast({ kind: 'success', text: r.seeded ? `Seeded ${r.seeded} cost center${r.seeded > 1 ? 's' : ''} from actuals + budget.` : 'Nothing to seed — no budget or actuals for your cost centers.' })
      await load(fy)
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) } finally { setSeeding(false) }
  }

  const totals = useMemo(() => {
    const rows = report?.rows ?? []
    const elapsed = report?.elapsedMonths ?? 0
    let forecast = 0, budget = 0, actual = 0
    for (const row of rows) {
      forecast += sum(forecastOf(row.brand))
      budget += sum(row.budget)
      actual += sum(row.actual, 0, elapsed)
    }
    return { forecast, budget, actual }
  }, [report, draft])

  if (report && report.rows.length === 0 && !loading)
    return <div className="page"><h1>Sales forecast</h1><div className="panel"><Empty>
      <strong>No cost centers to forecast</strong>
      <span>Create a budget for this year, or ask an administrator to assign you a cost center.</span>
    </Empty></div></div>

  const elapsed = report?.elapsedMonths ?? 0
  const canEditAny = (report?.rows ?? []).some(r => r.canEdit)

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Sales forecast</h1>
          <span className="muted small">
            Expected sales per cost center per month. Elapsed months seed from B1 actual sales, remaining months from the sales budget.
            {report?.budgetVersionName && <> Budget baseline: {report.budgetVersionName}.</>}
            {report?.actualsAsOf && <> Actuals read {when(report.actualsAsOf)}.</>}
          </span>
        </div>
        <div className="row">
          {report && report.availableYears.length > 0 && (
            <select value={fy} onChange={e => load(Number(e.target.value))} aria-label="Fiscal year">
              {report.availableYears.map(y => <option key={y} value={y}>FY {y}</option>)}
            </select>
          )}
          <button disabled={loading} onClick={() => load(fy, true)}>{loading ? 'Loading…' : 'Refresh actuals'}</button>
          {canEditAny && <button disabled={seeding || loading} onClick={seed}>{seeding ? 'Seeding…' : 'Seed from actuals + budget'}</button>}
          {canEditAny && <button className="primary" disabled={saving || dirty.size === 0} onClick={save}>{saving ? 'Saving…' : dirty.size ? `Save (${dirty.size})` : 'Saved'}</button>}
        </div>
      </div>

      {error && <div className="banner error">{error}</div>}
      {!report && !error && <div className="empty">Loading forecast…</div>}

      {report && report.rows.length > 0 && <>
        <div className="tiles">
          <div className="tile">
            <div className="label">Forecast sales (FY)</div>
            <div className="value">{fmt(totals.forecast)}</div>
            <div className="sub">{report.currency || 'local'} · full year</div>
          </div>
          <div className="tile">
            <div className="label">Budget sales (FY)</div>
            <div className="value">{fmt(totals.budget)}</div>
            <div className="sub">{report.budgetVersionName ?? 'no budget baseline'}</div>
          </div>
          <div className="tile">
            <div className="label">Forecast vs budget</div>
            {(() => { const v = vsBudget(totals.forecast, totals.budget); return <>
              <div className={`value ${v >= 0 ? 'ok' : 'bad'}`}>{v >= 0 ? '+' : ''}{fmt(v)}</div>
              <div className="sub">{totals.budget ? pct(v / Math.abs(totals.budget) * 100) : '—'} vs budget</div>
            </> })()}
          </div>
          <div className="tile">
            <div className="label">Actual sales to date</div>
            <div className="value">{fmt(totals.actual)}</div>
            <div className="sub">{elapsed} elapsed month{elapsed === 1 ? '' : 's'} of FY {fy}</div>
          </div>
        </div>

        <div className="panel flush">
          <div className="panel-head">
            <h2>By cost center</h2>
            <span className="muted small">shaded months are elapsed (actuals) · click a cost center to compare with budget &amp; actual</span>
            <div className="grow" />
            {!canEditAny && <span className="status neutral">Read-only</span>}
          </div>
          <div className="panel-body table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th style={{ minWidth: 180 }}>Cost center</th>
                  {report.periodLabels.map((p, i) => <th key={i} className={`num${i < elapsed ? ' past' : ''}`} style={{ minWidth: 88 }}>{p}</th>)}
                  <th className="num">FY total</th>
                  <th className="num">vs budget</th>
                </tr>
              </thead>
              <tbody>
                {report.rows.map(row => {
                  const f = forecastOf(row.brand)
                  const ftot = sum(f), btot = sum(row.budget)
                  const v = vsBudget(ftot, btot)
                  const isOpen = expanded.has(row.brand)
                  const toggle = () => setExpanded(s => { const n = new Set(s); n.has(row.brand) ? n.delete(row.brand) : n.add(row.brand); return n })
                  return [
                    <tr key={row.brand} className={dirty.has(row.brand) ? 'dirty-row' : ''}>
                      <td>
                        <button className="link" onClick={toggle} style={{ marginRight: 4 }}>{isOpen ? '▾' : '▸'}</button>
                        <span className="code">{row.brand}</span> <span className="muted">{row.brandName}</span>
                        {!row.canEdit && <span className="small muted"> · read-only</span>}
                      </td>
                      {f.map((amt, i) => (
                        <td key={i} className={`num${i < elapsed ? ' past' : ''}`}>
                          {row.canEdit
                            ? <input className="cell" inputMode="decimal" value={cellValue(row.brand, i)}
                                onFocus={() => focusCell(row.brand, i)}
                                onChange={e => setCell(row.brand, i, e.target.value)} onBlur={() => commitCell(row.brand, i)} />
                            : (amt ? fmt(amt) : '')}
                        </td>
                      ))}
                      <td className="num"><strong>{fmt(ftot)}</strong></td>
                      <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{v > 0 ? '+' : ''}{fmt(v)}</td>
                    </tr>,
                    ...(isOpen ? [
                      <tr key={row.brand + 'b'} className="subtotal">
                        <td style={{ paddingLeft: 28 }}>Budget</td>
                        {row.budget.map((x, i) => <td key={i} className="num muted">{x ? fmt(x) : ''}</td>)}
                        <td className="num muted">{fmt(btot)}</td><td />
                      </tr>,
                      <tr key={row.brand + 'a'} className="subtotal">
                        <td style={{ paddingLeft: 28 }}>Actual</td>
                        {row.actual.map((x, i) => <td key={i} className={`num muted${i < elapsed ? '' : ' faint'}`}>{x ? fmt(x) : ''}</td>)}
                        <td className="num muted">{fmt(sum(row.actual, 0, elapsed))}</td><td />
                      </tr>,
                    ] : []),
                  ]
                })}
                <tr className="total">
                  <td>Total forecast sales</td>
                  {report.periodLabels.map((_, i) => <td key={i} className="num">{fmt(report.rows.reduce((s, r) => s + forecastOf(r.brand)[i], 0))}</td>)}
                  <td className="num">{fmt(totals.forecast)}</td>
                  <td className={`num ${totals.forecast - totals.budget >= 0 ? 'ok' : 'bad'}`}>
                    {totals.forecast - totals.budget >= 0 ? '+' : ''}{fmt(totals.forecast - totals.budget)}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div className="muted small">
          Full-year totals: forecast {short(totals.forecast)} · budget {short(totals.budget)}. The forecast is app-only — it is not pushed to SAP B1.
        </div>
      </>}
      {toastNode}
    </div>
  )
}
