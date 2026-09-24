export type B1Mode = 'Mock' | 'ServiceLayer'
export type AccountKind = 'Revenue' | 'Expense' | 'Other'
export type VersionStatus = 'Draft' | 'Approved' | 'Pushed' | 'Superseded'
export type PushStatus = 'Running' | 'Succeeded' | 'PartiallyFailed' | 'Failed'
export type PushAction = 'Created' | 'Updated' | 'Unchanged' | 'Failed' | 'Cleared'

/** One SAP B1 company database, with its connection and budget conventions. */
export interface Company {
  id: number
  name: string
  currency: string
  groupRate: number
  budgetCount: number
  mode: B1Mode
  serviceLayerUrl: string
  companyDb: string
  userName: string
  hasPassword: boolean
  ignoreSslErrors: boolean
  dimension: number
  fiscalYearStartMonth: number
  scenarioNamePattern: string
  pushMainBudget: boolean
  mainScenarioName: string
  fieldMapOverrideJson: string | null
  masterDataSyncedAt: string | null
}
/** @deprecated kept so pages written against the single-company settings keep compiling */
export type Settings = Company
export type CompanyForm = Omit<Company, 'id' | 'hasPassword' | 'masterDataSyncedAt' | 'budgetCount'> & { password?: string | null }

// ---------------------------------------------------------------- users & workflow

export type UserRole = 'User' | 'Manager' | 'Admin'
export interface DepartmentRef { companyId: number; brandCode: string }
export interface Me {
  id: number
  userName: string
  displayName: string
  email: string | null
  role: UserRole
  managerId: number | null
  mustChangePassword: boolean
  departments: DepartmentRef[]
}
export interface AppUser extends Omit<Me, 'mustChangePassword'> {
  active: boolean
  mustChangePassword: boolean
  createdAt: string
  lastLoginAt: string | null
}
export interface UserForm {
  userName: string
  displayName: string
  email: string | null
  role: UserRole
  managerId: number | null
  active: boolean
  password: string | null
  departments: DepartmentRef[]
}

export type DeptStatus = 'Draft' | 'Submitted' | 'Approved' | 'Rejected'
export interface DeptView {
  brand: string
  brandName: string
  owners: string[]
  status: DeptStatus
  submittedBy: string | null
  submittedAt: string | null
  approver: string | null
  decidedBy: string | null
  decidedAt: string | null
  comment: string | null
  lineCount: number
  revenue: number
  expense: number
  canEdit: boolean
  canSubmit: boolean
  canApprove: boolean
  canReject: boolean
  canReopen: boolean
}
export interface DeptEvent { action: 'Submitted' | 'Approved' | 'Rejected' | 'Reopened'; at: string; comment: string | null; user: string }
export interface InboxItem {
  versionId: number
  brand: string
  companyId: number
  companyName: string
  budget: string
  fiscalYear: number
  submittedBy: string | null
  submittedAt: string
  comment: string | null
  assignedToMe: boolean
}

export interface MailSettings {
  enabled: boolean
  host: string
  port: number
  security: 'Auto' | 'StartTls' | 'SslOnConnect' | 'None'
  userName: string | null
  hasPassword: boolean
  fromAddress: string
  fromName: string
  appUrl: string
}
export interface MailLogEntry { id: number; at: string; to: string; subject: string; status: 'Sent' | 'Failed' | 'Skipped'; error: string | null }

export interface Brand { code: string; name: string; active: boolean }
export interface Account { code: string; name: string; kind: AccountKind; displayCode: string | null }

export interface VersionSummary {
  id: number
  companyId: number
  familyId: number
  revisionNo: number
  parentVersionId: number | null
  supersededAt: string | null
  fiscalYear: number
  name: string
  notes: string | null
  status: VersionStatus
  createdAt: string
  updatedAt: string
  approvedAt: string | null
  lastPushedAt: string | null
  lineCount: number
  brandCount: number
  revenue: number
  expense: number
}

export interface Line { brandCode: string; accountCode: string; amounts: number[] }

export interface PushRunSummary {
  id: number
  versionId: number
  startedAt: string
  finishedAt: string | null
  status: PushStatus
  mode: B1Mode
  message: string | null
  total: number
  failed: number
}

export interface PushItem {
  id: number
  brandCode: string
  accountCode: string
  scenario: string
  action: PushAction
  annual: number
  b1Key: string | null
  error: string | null
}

export interface PushRun extends Omit<PushRunSummary, 'total' | 'failed'> { items: PushItem[] }

export interface BvaRow {
  brand: string
  brandName: string
  account: string
  accountName: string
  kind: AccountKind
  budget: number[]
  actual: number[]
  compare: number[] | null
}

export interface BvaReport {
  versionId: number
  versionName: string
  fiscalYear: number
  fromPeriod: number
  toPeriod: number
  periodLabels: string[]
  currentPeriod: number
  actualsAsOf: string
  rows: BvaRow[]
  compareVersionId: number | null
  compareVersionName: string | null
}

export interface JournalLine {
  transId: number
  lineId: number
  date: string
  account: string
  brand: string
  debit: number
  credit: number
  memo: string | null
  ref1: string | null
  transType: string | null
}

export interface FieldMap { [k: string]: unknown; notes: string[]; fromMetadata: boolean }

export type ChangeKind = 'Added' | 'Removed' | 'Changed' | 'Same'
export interface CompareRow { brand: string; account: string; a: number[]; b: number[]; change: ChangeKind }
export interface CompareResult { a: VersionSummary; b: VersionSummary; rows: CompareRow[] }

export interface ReconcileRow {
  brand: string
  scenario: string
  account?: string
  app?: number
  b1?: number | null
  match?: boolean
  error?: string
}

export interface GroupCompanyPart { companyId: number; budget: number[]; actual: number[] }
export interface GroupRow {
  brand: string
  brandName: string
  account: string
  accountName: string
  kind: AccountKind
  budget: number[]
  actual: number[]
  byCompany: GroupCompanyPart[]
}
export interface GroupCompany {
  id: number
  name: string
  currency: string
  groupRate: number
  versionId: number | null
  versionLabel: string | null
  options: { id: number; label: string }[]
  error: string | null
}
export interface GroupReport {
  fiscalYear: number
  fromPeriod: number
  toPeriod: number
  periodLabels: string[]
  currentPeriod: number
  companies: GroupCompany[]
  rows: GroupRow[]
}

// ---------------------------------------------------------------- active company

const COMPANY_KEY = 'nexus-budget.company'
let companyId: number | null = (() => {
  try { const v = localStorage.getItem(COMPANY_KEY); return v ? Number(v) : null } catch { return null }
})()

export const getCompanyId = () => companyId

/** Switch the active company; every later request is scoped to it. Fires "company-changed" on window. */
export function setCompanyId(id: number) {
  if (id === companyId) return
  companyId = id
  try { localStorage.setItem(COMPANY_KEY, String(id)) } catch { /* private mode */ }
  window.dispatchEvent(new CustomEvent('company-changed', { detail: id }))
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, headers: companyId ? { 'X-Company-Id': String(companyId) } : {} }
  if (body instanceof FormData) init.body = body
  else if (body !== undefined) {
    init.body = JSON.stringify(body)
    ;(init.headers as Record<string, string>)['Content-Type'] = 'application/json'
  }
  const res = await fetch('/api' + url, init)
  const text = await res.text()
  let data: { message?: string } | undefined
  try { data = text ? JSON.parse(text) : undefined } catch { data = undefined }
  // Session expired or signed out elsewhere — the app shell shows the login screen.
  if (res.status === 401 && !url.startsWith('/auth/')) window.dispatchEvent(new Event('auth-required'))
  if (!res.ok) throw new Error(data?.message ?? `${res.status} ${res.statusText}`)
  return data as T
}

export const api = {
  authState: () => request<{ needsSetup: boolean; user: Me | null }>('GET', '/auth/state'),
  setup: (r: { userName: string; displayName: string; password: string }) => request<Me>('POST', '/auth/setup', r),
  login: (userName: string, password: string) => request<Me>('POST', '/auth/login', { userName, password }),
  logout: () => request<void>('POST', '/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string) => request<void>('POST', '/auth/password', { currentPassword, newPassword }),
  users: () => request<AppUser[]>('GET', '/users'),
  directory: () => request<{ id: number; displayName: string; role: UserRole }[]>('GET', '/users/directory'),
  createUser: (u: UserForm) => request<{ id: number }>('POST', '/users', u),
  saveUser: (id: number, u: UserForm) => request<void>('PUT', `/users/${id}`, u),
  brandsOf: (companyId: number) => request<Brand[]>('GET', `/brands?companyId=${companyId}&all=true`),
  departments: (versionId: number) => request<DeptView[]>('GET', `/versions/${versionId}/departments`),
  deptAction: (versionId: number, brand: string, action: 'submit' | 'approve' | 'reject' | 'reopen', comment?: string) =>
    request<void>('POST', `/versions/${versionId}/departments/${encodeURIComponent(brand)}/${action}`, { comment: comment ?? null }),
  deptEvents: (versionId: number, brand: string) => request<DeptEvent[]>('GET', `/versions/${versionId}/departments/${encodeURIComponent(brand)}/events`),
  inbox: () => request<InboxItem[]>('GET', '/approvals/inbox'),
  mailSettings: () => request<MailSettings>('GET', '/mail/settings'),
  saveMailSettings: (s: Omit<MailSettings, 'hasPassword'> & { password: string | null; clearPassword: boolean }) =>
    request<MailSettings>('PUT', '/mail/settings', s),
  testMail: (to: string) => request<{ message: string }>('POST', '/mail/test', { to }),
  mailLog: () => request<MailLogEntry[]>('GET', '/mail/log'),

  companies: () => request<Company[]>('GET', '/companies'),
  createCompany: (c: CompanyForm) => request<Company>('POST', '/companies', c),
  saveCompany: (id: number, c: CompanyForm) => request<Company>('PUT', `/companies/${id}`, c),
  deleteCompany: (id: number) => request<void>('DELETE', `/companies/${id}`),
  testConnection: (id: number) => request<{ message: string }>('POST', `/companies/${id}/test`),
  budgetFields: (id: number) => request<FieldMap>('GET', `/companies/${id}/budget-fields`),
  syncMasterData: (id: number) => request<{ brands: number; accounts: number }>('POST', `/companies/${id}/sync`),
  brands: () => request<Brand[]>('GET', '/brands'),
  accounts: () => request<Account[]>('GET', '/accounts'),

  versions: () => request<VersionSummary[]>('GET', '/versions'),
  version: (id: number) => request<{ version: VersionSummary; lines: Line[] }>('GET', `/versions/${id}`),
  createVersion: (r: { fiscalYear: number; name: string; notes?: string; copyFromVersionId?: number | null }) =>
    request<{ id: number }>('POST', '/versions', r),
  updateVersion: (id: number, r: { name: string; notes: string | null }) => request<void>('PUT', `/versions/${id}`, r),
  deleteVersion: (id: number) => request<void>('DELETE', `/versions/${id}`),
  approve: (id: number) => request<void>('POST', `/versions/${id}/status/Approved`),
  revise: (id: number, notes?: string) => request<{ id: number; revisionNo: number }>('POST', `/versions/${id}/revise`, { notes }),
  revisions: (id: number) => request<VersionSummary[]>('GET', `/versions/${id}/revisions`),
  compare: (a: number, b: number) => request<CompareResult>('GET', `/compare?a=${a}&b=${b}`),
  saveBrandLines: (id: number, brand: string, lines: Line[]) =>
    request<void>('PUT', `/versions/${id}/brands/${encodeURIComponent(brand)}/lines`, { lines }),
  removeOrphans: (id: number) => request<{ removed: number }>('DELETE', `/versions/${id}/orphan-lines`),
  seed: (id: number, r: { sourceFiscalYear: number; upliftPercent: number; brands?: string[]; overwrite: boolean }) =>
    request<{ added: number; replaced: number }>('POST', `/versions/${id}/seed`, r),
  importExcel: (id: number, file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return request<{ imported: number; brands: number; errors: string[] }>('POST', `/versions/${id}/import`, fd)
  },
  exportUrl: (id: number) => `/api/versions/${id}/export`,
  push: (id: number, brands?: string[]) => request<{ runId: number }>('POST', `/versions/${id}/push`, { brands }),
  pushRuns: (versionId?: number) => request<PushRunSummary[]>('GET', `/push-runs${versionId ? `?versionId=${versionId}` : ''}`),
  pushRun: (id: number) => request<{ run: PushRun; expected: number }>('GET', `/push-runs/${id}`),
  reconcile: (id: number) => request<ReconcileRow[]>('GET', `/versions/${id}/reconcile`),

  bva: (versionId: number, from?: number, to?: number, refresh = false, compareVersionId?: number) =>
    request<BvaReport>('GET', `/reports/bva?versionId=${versionId}${from ? `&from=${from}` : ''}${to ? `&to=${to}` : ''}${refresh ? '&refresh=true' : ''}${compareVersionId ? `&compareVersionId=${compareVersionId}` : ''}`),
  group: (year: number, from?: number, to?: number, refresh = false, versions?: Record<number, number>) =>
    request<GroupReport>('GET', `/reports/group?year=${year}${from ? `&from=${from}` : ''}${to ? `&to=${to}` : ''}${refresh ? '&refresh=true' : ''}${
      versions && Object.keys(versions).length ? `&versions=${Object.entries(versions).map(([c, v]) => `${c}:${v}`).join(',')}` : ''}`),
  drill: (versionId: number, brand: string, account: string, from: number, to: number) =>
    request<JournalLine[]>('GET', `/reports/drill?versionId=${versionId}&brand=${encodeURIComponent(brand)}&account=${encodeURIComponent(account)}&from=${from}&to=${to}`),
}
