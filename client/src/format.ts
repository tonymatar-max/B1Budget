const num0 = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 })
const num2 = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })

export const fmt = (n: number) => num0.format(Math.round(n))
export const fmt2 = (n: number) => num2.format(n)
export const pct = (n: number | null) => (n === null || !isFinite(n) ? '—' : `${n.toFixed(1)}%`)

/** Short money: 1.2M, 340K. */
export const short = (n: number) => {
  const a = Math.abs(n)
  if (a >= 1e6) return `${(n / 1e6).toFixed(a >= 1e7 ? 0 : 1)}M`
  if (a >= 1e3) return `${(n / 1e3).toFixed(a >= 1e4 ? 0 : 1)}K`
  return fmt(n)
}

export const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—')
export const day = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' })

export const sum = (a: number[], from = 0, to = a.length) => a.slice(from, to).reduce((s, x) => s + x, 0)

/** Parse user input like "12,500" or "1.5k" into a number; returns null if not numeric. */
export function parseAmount(s: string): number | null {
  const t = s.trim().toLowerCase().replace(/,/g, '')
  if (t === '') return 0
  const m = /^(-?\d*\.?\d+)\s*([km])?$/.exec(t)
  if (!m) return null
  const v = parseFloat(m[1]) * (m[2] === 'k' ? 1e3 : m[2] === 'm' ? 1e6 : 1)
  return Math.round(v * 100) / 100
}

export const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
