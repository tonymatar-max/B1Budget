import { useEffect, useMemo, useRef, useState } from 'react'
import { api, type ForecastReport, type ForecastBasis, type ForecastMethod, type ForecastMeasure } from '../api'
import { useAuth } from '../auth'
import { fmt, fq, parseAmount, pct, short, sum, when } from '../format'
import { Empty, useToast } from '../ui'

/** Favourable sales variance is positive: forecasting/selling more than budget is good. */
const vsBudget = (forecast: number, budget: number) => forecast - budget

const BASES: { value: ForecastBasis; label: string }[] = [
  { value: 'Dimension', label: 'Cost center (dimension)' },
  { value: 'ItemGroup', label: 'Item group' },
  { value: 'Item', label: 'Item' },
  { value: 'ItemUdf', label: 'Item UDF' },
]

const METHODS: { value: ForecastMethod; label: string; needsGrowth?: boolean; needsYears?: boolean; dimOnly?: boolean; hint: string }[] = [
  { value: 'Budget', label: 'Budget', dimOnly: true, hint: 'Remaining months = the sales budget' },
  { value: 'RunRate', label: 'Run-rate (YTD × 12)', hint: 'Remaining months = the year-to-date monthly average' },
  { value: 'PriorYearsAverage', label: 'Previous years average', needsGrowth: true, needsYears: true, hint: 'Months = the per-month average of the previous X years × (1 + growth%); fills a whole future year' },
  { value: 'PriorYearGrowth', label: 'Prior year + growth %', needsGrowth: true, hint: "Remaining months = last year's same month × (1 + growth%)" },
  { value: 'SeasonalRunRate', label: 'Seasonal (last-year shape)', hint: "Remaining months = last year's shape scaled by this year's pace" },
  { value: 'LinearTrend', label: 'Linear trend', hint: 'Remaining months = straight-line regression through the elapsed months' },
]

export default function ForecastPage({ year }: { year?: number }) {
  const auth = useAuth()
  const [basis, setBasis] = useState<ForecastBasis>('Dimension')
  const [udf, setUdf] = useState('')
  const [group, setGroup] = useState('')
  const [measure, setMeasure] = useState<ForecastMeasure>('Value')
  const [method, setMethod] = useState<ForecastMethod>('Budget')
  const [growth, setGrowth] = useState(5)
  const [avgYears, setAvgYears] = useState(3)
  const [yearText, setYearText] = useState('')
  const [yearSel, setYearSel] = useState<number | undefined>(year)
  const [report, setReport] = useState<ForecastReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draft, setDraft] = useState<Record<string, number[]>>({})
  const [dirty, setDirty] = useState<Set<string>>(new Set())
  const [editing, setEditing] = useState<Record<string, string>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [seeding, setSeeding] = useState(false)
  const [importing, setImporting] = useState(false)
  const [importErrors, setImportErrors] = useState<string[]>([])
  const fileRef = useRef<HTMLInputElement>(null)
  const [, setToast, toastNode] = useToast()

  const load = async (refresh = false) => {
    setLoading(true); setError(null)
    try {
      const r = await api.forecast(yearSel, basis, udf, group, measure, refresh)
      // Item-UDF basis needs a field chosen: default to the first one B1 offers, which reloads.
      if (r.basis === 'ItemUdf' && !udf && r.udfFields.length) { setUdf(r.udfFields[0]); return }
      setReport(r)
      setYearSel(r.fiscalYear)
      setYearText(String(r.fiscalYear))
      setDraft(Object.fromEntries(r.rows.map(row => [row.brand, [...row.forecast]])))
      setDirty(new Set()); setEditing({}); setExpanded(new Set())
    } catch (e) { setError((e as Error).message) } finally { setLoading(false) }
  }
  useEffect(() => { load() }, [basis, udf, group, measure, yearSel])
  // The Budget method only exists on the cost-center basis with a baseline; move off it otherwise.
  useEffect(() => {
    if (report && !report.hasBudgetBaseline && method === 'Budget') setMethod('RunRate')
  }, [report, method])

  const fy = report?.fiscalYear
  const hasBudget = report?.hasBudgetBaseline ?? false
  const memberLabel = report?.memberLabel ?? 'Cost center'
  // Lowercase for mid-sentence use — but a UDF field name (e.g. "U_Brand") stays as-is.
  const mll = basis === 'ItemUdf' ? memberLabel : memberLabel.toLowerCase()
  // Quantity mode (Item basis only): units instead of currency, so keep decimals and relabel.
  const isQty = basis === 'Item' && (report?.measure ?? measure) === 'Quantity'
  const nf = isQty ? fq : fmt
  const noun = isQty ? 'quantity' : 'sales'
  const forecastOf = (b: string) => draft[b] ?? report?.rows.find(r => r.brand === b)?.forecast ?? Array(12).fill(0)

  const changeBasis = (b: ForecastBasis) => { setReport(null); setUdf(''); setGroup(''); setMeasure('Value'); setBasis(b) }

  const setCell = (brand: string, i: number, raw: string) => {
    setEditing(e => ({ ...e, [`${brand}:${i}`]: raw }))
    const n = parseAmount(raw)
    if (n === null) return
    setDraft(d => { const next = [...(d[brand] ?? forecastOf(brand))]; next[i] = n; return { ...d, [brand]: next } })
    setDirty(s => new Set(s).add(brand))
  }
  const focusCell = (brand: string, i: number) => setEditing(e => { const v = forecastOf(brand)[i]; return { ...e, [`${brand}:${i}`]: v ? String(v) : '' } })
  const commitCell = (brand: string, i: number) => setEditing(e => { const n = { ...e }; delete n[`${brand}:${i}`]; return n })
  const cellValue = (brand: string, i: number) => {
    const k = `${brand}:${i}`
    if (k in editing) return editing[k]
    const v = forecastOf(brand)[i]
    return v ? nf(v) : ''
  }

  const save = async () => {
    if (!fy || dirty.size === 0) return
    setSaving(true)
    try {
      for (const brand of dirty) await api.saveForecastLine(fy, brand, forecastOf(brand), basis, udf, measure)
      setToast({ kind: 'success', text: `Saved ${dirty.size} ${mll}${dirty.size > 1 ? 's' : ''}.` })
      await load()
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) } finally { setSaving(false) }
  }

  const seed = async () => {
    if (!fy) return
    const md = METHODS.find(m => m.value === method)!
    if (dirty.size && !confirm(`Seeding overwrites your rows (elapsed months = actuals; ${md.hint.toLowerCase()}). Unsaved edits will be lost. Continue?`)) return
    setSeeding(true)
    try {
      const r = await api.seedForecast(fy, basis, udf, group, measure, method, growth, avgYears, true)
      setToast({ kind: 'success', text: r.seeded ? `Seeded ${r.seeded} ${mll}${r.seeded > 1 ? 's' : ''} · ${md.label}.` : 'Nothing to seed — no actuals or prior-year data for these members.' })
      await load()
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) } finally { setSeeding(false) }
  }

  const exportExcel = async () => {
    if (!fy) return
    try {
      const { blob, name } = await api.exportForecast(fy, basis, udf, group, measure)
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a'); a.href = url; a.download = name; a.click()
      URL.revokeObjectURL(url)
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
  }

  const importExcel = async (file?: File) => {
    if (fileRef.current) fileRef.current.value = ''   // allow picking the same file again
    if (!file || !fy) return
    if (dirty.size && !confirm('Importing replaces the rows in the file and reloads the grid. Your unsaved edits will be lost. Continue?')) return
    setImporting(true); setImportErrors([])
    try {
      const r = await api.importForecast(fy, basis, udf, measure, file)
      setImportErrors(r.errors)
      setToast({
        kind: r.errors.length ? 'info' : 'success',
        text: `Imported ${r.imported} ${mll}${r.imported === 1 ? '' : 's'}${r.skipped ? `, skipped ${r.skipped} (see below)` : ''}.`,
      })
      await load()
    } catch (e) { setToast({ kind: 'error', text: (e as Error).message }) } finally { setImporting(false) }
  }

  const totals = useMemo(() => {
    const rows = report?.rows ?? []
    const elapsed = report?.elapsedMonths ?? 0
    let forecast = 0, budget = 0, actual = 0
    for (const row of rows) { forecast += sum(forecastOf(row.brand)); budget += sum(row.budget); actual += sum(row.actual, 0, elapsed) }
    return { forecast, budget, actual }
  }, [report, draft])

  const elapsed = report?.elapsedMonths ?? 0
  const canEditAny = (report?.rows ?? []).some(r => r.canEdit)

  return (
    <div className="page">
      <div className="page-head">
        <div className="titles">
          <h1>Sales forecast</h1>
          <span className="muted small">
            Expected {noun} per {mll} per month. Elapsed months seed from B1 actual {noun};
            remaining months from the {hasBudget ? 'sales budget' : 'elapsed-month run-rate'}.
            {report?.budgetVersionName && <> Budget baseline: {report.budgetVersionName}.</>}
            {report?.actualsAsOf && <> Actuals read {when(report.actualsAsOf)}.</>}
          </span>
        </div>
        <div className="row">
          {auth.isAdmin && (
            <select value={basis} onChange={e => changeBasis(e.target.value as ForecastBasis)} aria-label="Forecast by" title="What to forecast by">
              {BASES.map(b => <option key={b.value} value={b.value}>{b.label}</option>)}
            </select>
          )}
          {auth.isAdmin && basis === 'ItemUdf' && (report?.udfFields.length
            ? <select value={udf} onChange={e => setUdf(e.target.value)} aria-label="Item UDF" title="Item user-defined field">
                {report.udfFields.map(f => <option key={f} value={f}>{f}</option>)}
              </select>
            : <span className="status warn" title="No U_ fields found on the item master">No item UDFs</span>)}
          {auth.isAdmin && basis === 'Item' && report && (
            <select value={group} onChange={e => setGroup(e.target.value)} aria-label="Item group filter" title="Filter active items by item group">
              <option value="">All item groups</option>
              {report.itemGroups.map(g => <option key={g.code} value={g.code}>{g.name}</option>)}
            </select>
          )}
          {auth.isAdmin && basis === 'Item' && (
            <div className="seg" title="Forecast the sales value, or the quantity (units) sold">
              {(['Value', 'Quantity'] as ForecastMeasure[]).map(m =>
                <button key={m} className={measure === m ? 'on' : ''} disabled={loading} onClick={() => setMeasure(m)}>{m}</button>)}
            </div>
          )}
          {report && <>
            <select value={report.availableYears.includes(fy ?? 0) ? fy : ''}
              onChange={e => { if (e.target.value) setYearSel(Number(e.target.value)) }} aria-label="Fiscal year" title="Years with a budget, forecast, or recent">
              {!report.availableYears.includes(fy ?? 0) && <option value="">FY {fy}</option>}
              {report.availableYears.map(y => <option key={y} value={y}>FY {y}</option>)}
            </select>
            <input type="number" value={yearText} min={2000} max={2100} aria-label="Go to year" title="Type any year, then Enter"
              style={{ width: 78 }}
              onChange={e => setYearText(e.target.value)}
              onBlur={() => { const y = Number(yearText); if (y >= 2000 && y <= 2100 && y !== fy) setYearSel(y) }}
              onKeyDown={e => { if (e.key === 'Enter') { const y = Number(yearText); if (y >= 2000 && y <= 2100) setYearSel(y) } }} />
          </>}
          <button disabled={loading} onClick={() => load(true)}>{loading ? 'Loading…' : 'Refresh actuals'}</button>
          <button disabled={loading || !fy || !report || report.rows.length === 0} onClick={exportExcel} title="Download this grid as an Excel file">Export Excel</button>
          {canEditAny && <>
            <input ref={fileRef} type="file" accept=".xlsx" hidden onChange={e => importExcel(e.target.files?.[0])} />
            <button disabled={importing || loading || !fy} onClick={() => fileRef.current?.click()} title="Import monthly figures from an Excel file (same layout as Export)">{importing ? 'Importing…' : 'Import Excel'}</button>
            <select value={method} onChange={e => setMethod(e.target.value as ForecastMethod)} aria-label="Forecast method"
              title={METHODS.find(m => m.value === method)?.hint}>
              {METHODS.filter(m => !m.dimOnly || hasBudget).map(m => <option key={m.value} value={m.value}>{m.label}</option>)}
            </select>
            {METHODS.find(m => m.value === method)?.needsYears && (
              <label className="row" style={{ gap: 4, flexWrap: 'nowrap' }} title="How many previous years to average">
                <input type="number" min={1} max={10} value={avgYears} onChange={e => setAvgYears(Math.min(10, Math.max(1, Number(e.target.value) || 1)))}
                  style={{ width: 52 }} aria-label="Years to average" />
                <span className="muted small">yrs</span>
              </label>
            )}
            {METHODS.find(m => m.value === method)?.needsGrowth && (
              <label className="row" style={{ gap: 4, flexWrap: 'nowrap' }} title="Year-over-year growth applied to the baseline">
                <input type="number" step="0.5" value={growth} onChange={e => setGrowth(Number(e.target.value) || 0)}
                  style={{ width: 64 }} aria-label="Growth percent" />
                <span className="muted small">% growth</span>
              </label>
            )}
            <button disabled={seeding || loading} onClick={seed} title={METHODS.find(m => m.value === method)?.hint}>{seeding ? 'Seeding…' : 'Seed'}</button>
          </>}
          {canEditAny && <button className="primary" disabled={saving || dirty.size === 0} onClick={save}>{saving ? 'Saving…' : dirty.size ? `Save (${dirty.size})` : 'Saved'}</button>}
        </div>
      </div>

      {error && <div className="banner error">{error}</div>}
      {importErrors.length > 0 && (
        <div className="banner warn">
          <strong>{importErrors.length} row{importErrors.length === 1 ? '' : 's'} not imported:</strong>
          <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>{importErrors.slice(0, 8).map((e, i) => <li key={i}>{e}</li>)}</ul>
          {importErrors.length > 8 && <div className="small">…and {importErrors.length - 8} more.</div>}
          <button className="link small" onClick={() => setImportErrors([])}>Dismiss</button>
        </div>
      )}
      {report?.actualsError && <div className="banner warn">Couldn't read actual sales from SAP B1: {report.actualsError}</div>}
      {!report && !error && <div className="empty">Loading forecast…</div>}
      {report && report.rows.length === 0 && !loading && (
        <div className="panel"><Empty>
          <strong>Nothing to forecast here</strong>
          <span>{report.actualsError
            ? `Reading sales for FY ${fy} failed — see the message above. For item/item-group/UDF bases, SAP B1 Service Layer must allow OINV, INV1, ORIN, RIN1, OITM and OITB in its SQL whitelist (b1s_sqltable.conf).`
            : basis === 'Dimension'
              ? 'Create a budget for this year, or ask an administrator to assign you a cost center.'
              : `No ${mll} ${noun} found for FY ${fy}. Pick a year that has posted sales invoices, or try another basis.`}</span>
        </Empty></div>
      )}

      {report && report.rows.length > 0 && <>
        <div className="tiles">
          <div className="tile">
            <div className="label">Forecast {noun} (FY)</div>
            <div className="value">{nf(totals.forecast)}</div>
            <div className="sub">{isQty ? 'units' : report.currency || 'local'} · full year</div>
          </div>
          {hasBudget && <div className="tile">
            <div className="label">Budget sales (FY)</div>
            <div className="value">{nf(totals.budget)}</div>
            <div className="sub">{report.budgetVersionName ?? 'no budget baseline'}</div>
          </div>}
          {hasBudget && <div className="tile">
            <div className="label">Forecast vs budget</div>
            {(() => { const v = vsBudget(totals.forecast, totals.budget); return <>
              <div className={`value ${v >= 0 ? 'ok' : 'bad'}`}>{v >= 0 ? '+' : ''}{nf(v)}</div>
              <div className="sub">{totals.budget ? pct(v / Math.abs(totals.budget) * 100) : '—'} vs budget</div>
            </> })()}
          </div>}
          <div className="tile">
            <div className="label">Actual {noun} to date</div>
            <div className="value">{nf(totals.actual)}</div>
            <div className="sub">{elapsed} elapsed month{elapsed === 1 ? '' : 's'} of FY {fy}</div>
          </div>
        </div>

        <div className="panel flush">
          <div className="panel-head">
            <h2>By {mll}</h2>
            <span className="muted small">shaded months are elapsed (actuals) · click a row to compare with {hasBudget ? 'budget & actual' : 'actual'}</span>
            <div className="grow" />
            {!canEditAny && <span className="status neutral">Read-only</span>}
          </div>
          <div className="panel-body table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th style={{ minWidth: 180 }}>{memberLabel}</th>
                  {report.periodLabels.map((p, i) => <th key={i} className={`num${i < elapsed ? ' past' : ''}`} style={{ minWidth: 88 }}>{p}</th>)}
                  <th className="num">FY total</th>
                  {hasBudget && <th className="num">vs budget</th>}
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
                            : (amt ? nf(amt) : '')}
                        </td>
                      ))}
                      <td className="num"><strong>{nf(ftot)}</strong></td>
                      {hasBudget && <td className={`num ${v > 0.5 ? 'ok' : v < -0.5 ? 'bad' : ''}`}>{v > 0 ? '+' : ''}{nf(v)}</td>}
                    </tr>,
                    ...(isOpen ? [
                      ...(hasBudget ? [
                        <tr key={row.brand + 'b'} className="subtotal">
                          <td style={{ paddingLeft: 28 }}>Budget</td>
                          {row.budget.map((x, i) => <td key={i} className="num muted">{x ? nf(x) : ''}</td>)}
                          <td className="num muted">{nf(btot)}</td><td />
                        </tr>,
                      ] : []),
                      <tr key={row.brand + 'a'} className="subtotal">
                        <td style={{ paddingLeft: 28 }}>Actual</td>
                        {row.actual.map((x, i) => <td key={i} className={`num muted${i < elapsed ? '' : ' faint'}`}>{x ? nf(x) : ''}</td>)}
                        <td className="num muted">{nf(sum(row.actual, 0, elapsed))}</td>{hasBudget && <td />}
                      </tr>,
                    ] : []),
                  ]
                })}
                <tr className="total">
                  <td>Total forecast {noun}</td>
                  {report.periodLabels.map((_, i) => <td key={i} className="num">{nf(report.rows.reduce((s, r) => s + forecastOf(r.brand)[i], 0))}</td>)}
                  <td className="num">{nf(totals.forecast)}</td>
                  {hasBudget && <td className={`num ${totals.forecast - totals.budget >= 0 ? 'ok' : 'bad'}`}>
                    {totals.forecast - totals.budget >= 0 ? '+' : ''}{nf(totals.forecast - totals.budget)}
                  </td>}
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div className="muted small">
          Full-year forecast {short(totals.forecast)}{hasBudget && <> · budget {short(totals.budget)}</>}.
          {basis !== 'Dimension' && (isQty ? ' Actual quantities come from posted sales invoice lines (minus credit memo lines).' : ' Actual sales come from posted sales invoices (minus credit memos).')}
          {' '}The forecast is app-only — it is not pushed to SAP B1.
        </div>
      </>}
      {toastNode}
    </div>
  )
}
