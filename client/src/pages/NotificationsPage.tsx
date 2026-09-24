import { useEffect, useState } from 'react'
import { api, type MailLogEntry, type MailSettings } from '../api'
import { useAuth } from '../auth'
import { when } from '../format'
import { useToast } from '../ui'

const presets: { label: string; host: string; port: number; security: MailSettings['security'] }[] = [
  { label: 'Microsoft 365', host: 'smtp.office365.com', port: 587, security: 'StartTls' },
  { label: 'Gmail / Google Workspace', host: 'smtp.gmail.com', port: 587, security: 'StartTls' },
  { label: 'Internal relay (no login)', host: '', port: 25, security: 'None' },
]

export default function NotificationsPage() {
  const { me } = useAuth()
  const [s, setS] = useState<MailSettings | null>(null)
  const [password, setPassword] = useState('')
  const [testTo, setTestTo] = useState(me.email ?? '')
  const [log, setLog] = useState<MailLogEntry[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [, setToast, toastNode] = useToast()

  const loadLog = () => api.mailLog().then(setLog).catch(() => {})
  useEffect(() => {
    api.mailSettings().then(x => setS({ ...x, appUrl: x.appUrl || window.location.origin })).catch(e => setToast({ kind: 'error', text: e.message }))
    loadLog()
  }, [])
  if (!s) return <div className="page"><div className="empty">Loading…</div></div>
  const set = <K extends keyof MailSettings>(k: K, v: MailSettings[K]) => setS({ ...s, [k]: v })

  const act = async (label: string, fn: () => Promise<string>) => {
    setBusy(label)
    try { setToast({ kind: 'success', text: await fn() }) }
    catch (e) { setToast({ kind: 'error', text: (e as Error).message }) }
    finally { setBusy(null); loadLog() }
  }

  const save = () => act('save', async () => {
    const r = await api.saveMailSettings({ ...s, password: password || null, clearPassword: false })
    setS(r); setPassword('')
    return r.enabled ? 'E-mail settings saved — notifications are on.' : 'Saved. Notifications are off.'
  })

  return (
    <div className="page" style={{ maxWidth: 1000 }}>
      <div className="page-head">
        <div className="titles">
          <h1>E-mail notifications</h1>
          <span className="muted">Approvers get an e-mail when a budget is submitted to them; owners when theirs is approved, rejected or reopened, and when the company budget is approved.</span>
        </div>
        <button className="primary" disabled={!!busy} onClick={save}>{busy === 'save' ? 'Saving…' : 'Save'}</button>
      </div>

      <div className="panel">
        <div className="panel-head">
          <h2>SMTP server</h2>
          <div className="grow" />
          <label className="check"><input type="checkbox" checked={s.enabled} onChange={e => set('enabled', e.target.checked)} /> <strong>Send notifications</strong></label>
        </div>
        <div className="panel-body stack">
          <div className="row small">
            <span className="muted">Presets:</span>
            {presets.map(p => <button key={p.label} className="link small" onClick={() => setS({ ...s, host: p.host || s.host, port: p.port, security: p.security })}>{p.label}</button>)}
          </div>
          <div className="form-grid">
            <label className="field" style={{ gridColumn: 'span 2' }}><span>SMTP server</span>
              <input value={s.host} onChange={e => set('host', e.target.value)} placeholder="smtp.office365.com" /></label>
            <label className="field"><span>Port</span><input type="number" value={s.port} onChange={e => set('port', Number(e.target.value))} /></label>
            <label className="field"><span>Encryption</span>
              <select value={s.security} onChange={e => set('security', e.target.value as MailSettings['security'])}>
                <option value="Auto">Automatic</option>
                <option value="StartTls">STARTTLS (usually port 587)</option>
                <option value="SslOnConnect">SSL/TLS (usually port 465)</option>
                <option value="None">None (internal relay)</option>
              </select></label>
            <label className="field"><span>User name</span><input value={s.userName ?? ''} onChange={e => set('userName', e.target.value)} autoComplete="off" placeholder="leave empty if no login" /></label>
            <label className="field"><span>Password {s.hasPassword && <em className="muted">(stored, encrypted — leave blank to keep)</em>}</span>
              <input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="new-password" /></label>
            <label className="field"><span>Sender address</span><input value={s.fromAddress} onChange={e => set('fromAddress', e.target.value)} placeholder="budget@company.com" /></label>
            <label className="field"><span>Sender name</span><input value={s.fromName} onChange={e => set('fromName', e.target.value)} /></label>
            <label className="field" style={{ gridColumn: 'span 2' }}><span>App address used in e-mail links</span>
              <input value={s.appUrl} onChange={e => set('appUrl', e.target.value)} placeholder="http://budget.company.local:5140" />
              <span className="small muted">The address people open the app at — not “localhost” unless everyone works on this server.</span></label>
          </div>
          <div className="row">
            <input value={testTo} onChange={e => setTestTo(e.target.value)} placeholder="you@company.com" style={{ width: 260 }} />
            <button disabled={!!busy || !testTo} onClick={() => act('test', async () => (await api.testMail(testTo)).message)}>
              {busy === 'test' ? 'Sending…' : 'Send test e-mail'}</button>
            <span className="small muted">Uses the saved settings — save first.</span>
          </div>
        </div>
      </div>

      <div className="panel flush">
        <div className="panel-head"><h2>Recent e-mails</h2><div className="grow" /><button className="link small" onClick={loadLog}>Refresh</button></div>
        <div className="panel-body table-wrap" style={{ maxHeight: 420 }}>
          {log.length === 0 ? <div className="empty">Nothing sent yet.</div> : (
            <table className="data">
              <thead><tr><th>When</th><th>To</th><th>Subject</th><th>Result</th></tr></thead>
              <tbody>{log.map(m => (
                <tr key={m.id}>
                  <td className="nowrap muted">{when(m.at)}</td><td>{m.to}</td><td>{m.subject}</td>
                  <td><span className={`status ${m.status === 'Sent' ? 'ok' : m.status === 'Failed' ? 'bad' : 'neutral'}`}>{m.status}</span>
                    {m.error && <div className="small muted">{m.error}</div>}</td>
                </tr>
              ))}</tbody>
            </table>
          )}
        </div>
      </div>
      {toastNode}
    </div>
  )
}
