# Nexus B1 Budget

Enter budgets **by brand (cost center) × G/L account × month**, push them into **SAP Business One native budgets**, and track **budget vs actual** with drill-down to journal lines.

- **Backend:** .NET 8 minimal API + SQLite (`server/`, port 5140)
- **Frontend:** React 19 + Vite + TS (`client/`, dev port 5175, builds into `server/wwwroot`)
- **SAP B1:** Service Layer only. Works on both HANA and SQL Server; no direct DB access.

## Run

```bash
cd server && dotnet run          # API + built UI on http://localhost:5140
cd client && npm install && npm run dev   # dev UI with hot reload on :5175
```

**Users & approvals.** Everyone signs in (cookie session, hashed passwords). On first start the app asks for the first **administrator**, which can only be done from a browser on the server itself. Administrators create users under **Users**:
- **Role**: *User* (budget owner), *Manager* (also approves their team), or *Admin* (finance: everything).
- **Departments**: the cost centers (per company) the person budgets.
- **Manager**: who approves their submissions. With no manager, administrators approve.

Inside a draft budget each department moves **Draft → Submitted → Approved** (or **Rejected** with a comment → edit → resubmit). Submitted and approved departments are locked. The approver or an admin can **Reopen** one. Every step is logged in the department's approval history. Owners and managers only see their own and their team's cost centers (budgets, reports, exports).

The **Global view** (admins) lists every cost center with owner, status and amounts. The whole budget can only be approved once every department with lines is approved; it is then pushed to SAP B1 as before. A new revision starts with all departments approved, and admins reopen only the ones that must change.

**E-mail notifications.** Configure SMTP under **Administration → E-mail** (presets for Microsoft 365 and Gmail, or an internal relay). Set the *app address* people use, because e-mail links point there. Notifications go to:
- the **approver** when a budget is submitted (the submitter's manager, or all admins if there is none);
- the **owner/submitter** when it is approved, rejected (with the reason) or reopened;
- every **department owner** when finance approves the whole budget.

Mail is sent in the background, so a slow or unreachable SMTP server never blocks an approval. Every attempt is shown in the *Recent e-mails* log, including users skipped because they have no e-mail address. **Send test e-mail** reports SMTP errors immediately.

**Multiple companies.** Each SAP B1 company database is a *company* in the app (**Companies** page), with its own Service Layer connection, brand dimension, fiscal-year start, scenario naming, brands, accounts, budgets, revisions and push history. The active company is picked in the top bar; every API call carries it in an `X-Company-Id` header. **Group report** combines all companies for one fiscal year: each company contributes its latest approved revision (or one you pick), converted with the company's *rate to group currency*, and the totals are summed by brand code and account code, with a per-company breakdown. A company that can't be reached shows its error while the rest of the report still loads.

The first start is in **Mock mode**: demo brands, accounts and actuals, and pushes are simulated. Switch to *SAP B1 Service Layer* under **Companies**, save, then **Test connection** → **Sync brands & accounts** → **Detect budget fields**.

## How it maps to SAP B1

| App | SAP B1 |
|---|---|
| Brand | Distribution rule (`OOCR`) in the configured dimension (1–5) |
| Budget revision × brand | **Budget scenario** (`OBGS`), named by pattern `{brand} {year}` |
| Budget revision, all brands | the year's **Main Budget** scenario gets the total per account (B1's budget checks use it) |
| Budget line (brand × account) | **Budget** (`OBGT`/`BGT1`) for that account in the brand's scenario, 12 period lines |
| Revenue account | amounts on the **credit** side; expense accounts on the **debit** side |
| Actuals | `JDT1` summed by account × dimension column × posting date, excluding period-end closing (`TransType -3`) |

- The push is a **sync**. Anything this app pushed earlier for the same fiscal year that the current revision no longer contains (a removed account or a whole brand) is zeroed and reported as *Cleared*. Budgets entered manually in B1 are never touched.
- The push is **idempotent**. Existing budgets are found by account + scenario and PATCHed in place (B1's own line objects are kept, only the amounts change). Budgets that already match are reported as *Unchanged*.
- Budget field names are **resolved from `$metadata`**, not hard-coded. If detection picks the wrong property, pin it with the JSON override in Settings (e.g. `{"LineDebit":"BudgetTotDebit"}`).
- Actuals and drill-down use **Service Layer SQLQueries** that the app creates and owns (`NXBGT_ACT{dim}`, `NXBGT_JE{dim}`, `NXBGT_JEN{dim}`). `JDT1` has to be allowed in Service Layer's SQL table whitelist (`b1s_sqltable.conf`).

## Workflow

1. **Budgets → New budget** (fiscal year and name). This is **Rev 1**.
2. Enter it in the grid per brand. Typing an annual amount in **Total** spreads it across the months. Alternatives: **Import Excel** or **Seed from actuals** (last year + x%).
3. **Approve** locks the revision for good. **Push to SAP B1** updates the brand scenarios and Main Budget; **Compare with B1** reconciles both.
4. To change an approved budget, use **Create revision**. It copies the approved revision into a draft (Rev 2, Rev 3…). Approving the draft marks the previous revision *Superseded*: it stays readable, but can't be pushed.
5. **Compare revisions** shows added, removed and changed lines with the monthly differences. Every revision's page has a revision switcher and a "Changes vs Rev N" button.
6. **Budget vs actual**: pick a revision and optionally a *second budget* (e.g. Rev 1). You get variance against both, plus the second budget as a line on the chart.

## Data

`server/data/budget.db` (SQLite) and `server/data/keys` (DataProtection keys; the SL passwords are encrypted with them). Back up both together. The keys are also tied to the app's folder, so after moving or redeploying the app you'll need to re-enter each company's password (the app says so clearly).

## Not yet verified against a live company

Everything above runs end to end in mock mode. Against a real B1 company, check these on first use:
- **Detect budget fields** shows the expected property names (`BudgetLines`, `BudgetTotDebit`/`BudgetTotCredit`).
- Creating a budget scenario via `POST BudgetScenarios` is allowed for the fiscal year (its posting periods must exist).
- The company uses **12 monthly** posting periods. Other period setups are rejected with a clear error.
