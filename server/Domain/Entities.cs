namespace B1Budget.Api.Domain;

public enum B1Mode { Mock, ServiceLayer }
public enum AccountKind { Revenue, Expense, Other }
public enum VersionStatus { Draft, Approved, Pushed, Superseded }
public enum PushStatus { Running, Succeeded, PartiallyFailed, Failed }
public enum PushAction { Created, Updated, Unchanged, Failed, Cleared }

/// <summary>
/// One SAP B1 company database the app budgets for: its connection and budget conventions.
/// Brands, accounts, budgets and push history all belong to a company.
/// (Stored in the "Settings" table — it began life as the single-company settings row.)
/// </summary>
public class Company
{
    public int Id { get; set; }
    /// <summary>Display name, e.g. "Kuwait" or "UAE Trading".</summary>
    public string Name { get; set; } = "Company";
    /// <summary>Local currency label shown in reports, e.g. "KWD".</summary>
    public string Currency { get; set; } = "";
    /// <summary>Multiply local amounts by this to get group currency in the group report.</summary>
    public decimal GroupRate { get; set; } = 1m;
    public B1Mode Mode { get; set; } = B1Mode.Mock;

    public string ServiceLayerUrl { get; set; } = "https://localhost:50000/b1s/v1";
    public string CompanyDb { get; set; } = "";
    public string UserName { get; set; } = "manager";
    public string? PasswordEncrypted { get; set; }
    public bool IgnoreSslErrors { get; set; } = true;

    /// <summary>Cost-accounting dimension (1–5) whose distribution rules are the brands.</summary>
    public int Dimension { get; set; } = 1;
    /// <summary>Calendar month (1–12) the fiscal year starts in.</summary>
    public int FiscalYearStartMonth { get; set; } = 1;
    /// <summary>B1 budget-scenario name for each brand. Tokens: {brand}, {year}.</summary>
    public string ScenarioNamePattern { get; set; } = "{brand} {year}";
    /// <summary>Also push the all-brand total per account into this scenario (B1's own budget checks use it).</summary>
    public bool PushMainBudget { get; set; } = true;
    public string MainScenarioName { get; set; } = "Main Budget";

    /// <summary>
    /// Optional JSON override of the Service Layer budget field names the push resolves from $metadata,
    /// e.g. {"LineDebit":"BudgetTotDebit"} — only needed if auto-detection picks the wrong property.
    /// </summary>
    public string? FieldMapOverrideJson { get; set; }

    public DateTime? MasterDataSyncedAt { get; set; }
}

public class Brand
{
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class GlAccount
{
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountKind Kind { get; set; }
    /// <summary>Formatted code / segment display, falls back to Code.</summary>
    public string? DisplayCode { get; set; }
}

/// <summary>
/// One revision of a budget. Revisions of the same budget share a FamilyId (= Id of revision 1).
/// Approved revisions are immutable; changes happen in a new revision copied from the latest one.
/// </summary>
public class BudgetVersion
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int FamilyId { get; set; }
    public int RevisionNo { get; set; } = 1;
    public int? ParentVersionId { get; set; }
    public DateTime? SupersededAt { get; set; }
    public int FiscalYear { get; set; }
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public VersionStatus Status { get; set; } = VersionStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? LastPushedAt { get; set; }

    public List<BudgetLine> Lines { get; set; } = new();
}

/// <summary>One brand × G/L account, with 12 fiscal-period amounts (always entered as positive budgets).</summary>
public class BudgetLine
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public string BrandCode { get; set; } = "";
    public string AccountCode { get; set; } = "";
    /// <summary>Index 0 = fiscal period 1.</summary>
    public decimal[] Amounts { get; set; } = new decimal[12];
}

public class PushRun
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public PushStatus Status { get; set; } = PushStatus.Running;
    public B1Mode Mode { get; set; }
    public string? Message { get; set; }
    public List<PushItem> Items { get; set; } = new();
}

public class PushItem
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string BrandCode { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string Scenario { get; set; } = "";
    public PushAction Action { get; set; }
    public decimal Annual { get; set; }
    public string? B1Key { get; set; }
    public string? Error { get; set; }
}

// ------------------------------------------------------------------ users & approval workflow

public enum UserRole { User, Manager, Admin }
public enum DeptStatus { Draft, Submitted, Approved, Rejected }
public enum DeptAction { Submitted, Approved, Rejected, Reopened }

/// <summary>A person who signs in. Budget owners are linked to departments (cost centers); their manager approves.</summary>
public class AppUser
{
    public int Id { get; set; }
    /// <summary>Sign-in name (usually the e-mail address), unique, case-insensitive.</summary>
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    /// <summary>Approver of this user's submissions. Null → any admin approves.</summary>
    public int? ManagerId { get; set; }
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public List<UserDepartment> Departments { get; set; } = new();
}

/// <summary>A department = one cost center (distribution rule) of one company.</summary>
public class UserDepartment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int CompanyId { get; set; }
    public string BrandCode { get; set; } = "";
}

/// <summary>Approval state of one department's section inside one budget revision.</summary>
public class DepartmentStatus
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public string BrandCode { get; set; } = "";
    public DeptStatus Status { get; set; } = DeptStatus.Draft;
    public int? SubmittedById { get; set; }
    public DateTime? SubmittedAt { get; set; }
    /// <summary>Who must approve: the submitter's manager (null → any admin).</summary>
    public int? ApproverId { get; set; }
    public int? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

/// <summary>Audit trail of the department workflow.</summary>
public class DepartmentEvent
{
    public int Id { get; set; }
    public int VersionId { get; set; }
    public string BrandCode { get; set; } = "";
    public DeptAction Action { get; set; }
    public int UserId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? Comment { get; set; }
}

// ------------------------------------------------------------------ e-mail notifications

public enum MailSecurity { Auto, StartTls, SslOnConnect, None }
public enum MailStatus { Sent, Failed, Skipped }

/// <summary>SMTP settings for workflow notifications (single row, Id = 1).</summary>
public class MailSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public MailSecurity Security { get; set; } = MailSecurity.Auto;
    public string? UserName { get; set; }
    public string? PasswordEncrypted { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Nexus B1 Budget";
    /// <summary>Base address people open the app at — used for links in e-mails.</summary>
    public string AppUrl { get; set; } = "http://localhost:5140";
}

/// <summary>What was sent (or why not) — shown to admins so a silent mail problem is visible.</summary>
public class MailLog
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public MailStatus Status { get; set; }
    public string? Error { get; set; }
}
