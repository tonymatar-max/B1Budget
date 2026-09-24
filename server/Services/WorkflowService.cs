using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public record DeptView(string Brand, string BrandName, List<string> Owners, DeptStatus Status,
    string? SubmittedBy, DateTime? SubmittedAt, string? Approver, string? DecidedBy, DateTime? DecidedAt, string? Comment,
    int LineCount, decimal Revenue, decimal Expense, decimal Unmapped,
    bool CanEdit, bool CanSubmit, bool CanApprove, bool CanReject, bool CanReopen);

/// <summary>
/// Department workflow inside a draft budget revision:
/// Draft → Submitted (by the owner) → Approved / Rejected (by the submitter's manager, or any admin).
/// Rejected goes back to editable; Submitted/Approved can be reopened by the approver or an admin while the
/// revision is still a draft. Once the whole revision is approved nothing is editable (create a revision instead).
/// </summary>
public class WorkflowService(AppDbContext db, Notifier notifier, ILogger<WorkflowService> log)
{
    public async Task<Dictionary<string, DepartmentStatus>> StatusesAsync(int versionId) =>
        await db.DepartmentStatuses.Where(d => d.VersionId == versionId).ToDictionaryAsync(d => d.BrandCode);

    public static DeptStatus StatusOf(Dictionary<string, DepartmentStatus> map, string brand) =>
        map.TryGetValue(brand, out var s) ? s.Status : DeptStatus.Draft;

    public static bool Editable(DeptStatus s) => s is DeptStatus.Draft or DeptStatus.Rejected;

    /// <summary>Throw unless the user may change this department's lines right now.</summary>
    public async Task RequireEditableAsync(BudgetVersion v, string brand, Scope scope)
    {
        if (v.Status != VersionStatus.Draft) throw new InvalidOperationException("Approved budgets are locked — create a revision to change it.");
        scope.Require(v.CompanyId, brand);
        var st = StatusOf(await StatusesAsync(v.Id), brand);
        if (!Editable(st))
            throw new InvalidOperationException(st == DeptStatus.Submitted
                ? $"{brand} is waiting for approval — it can't be changed until it is approved, rejected or reopened."
                : $"{brand} is already approved — ask your approver or an administrator to reopen it.");
    }

    /// <summary>Brands in this version the user may edit right now.</summary>
    public async Task<HashSet<string>> EditableBrandsAsync(BudgetVersion v, Scope scope)
    {
        if (v.Status != VersionStatus.Draft) return new();
        var map = await StatusesAsync(v.Id);
        var brands = await db.Brands.Where(b => b.CompanyId == v.CompanyId).Select(b => b.Code).ToListAsync();
        return brands.Where(b => scope.Has(v.CompanyId, b) && Editable(StatusOf(map, b))).ToHashSet();
    }

    public async Task<List<DeptView>> DepartmentsAsync(BudgetVersion v, Scope scope)
    {
        var map = await StatusesAsync(v.Id);
        var brands = await db.Brands.AsNoTracking().Where(b => b.CompanyId == v.CompanyId).ToDictionaryAsync(b => b.Code, b => b.Name);
        var kinds = await db.AccountKindsAsync(v.CompanyId);
        var lines = await db.Lines.AsNoTracking().Where(l => l.VersionId == v.Id).ToListAsync();
        var owners = (await db.UserDepartments.AsNoTracking().Where(d => d.CompanyId == v.CompanyId)
                .Join(db.Users.Where(u => u.Active), d => d.UserId, u => u.Id, (d, u) => new { d.BrandCode, u.DisplayName }).ToListAsync())
            .GroupBy(x => x.BrandCode).ToDictionary(g => g.Key, g => g.Select(x => x.DisplayName).OrderBy(n => n).ToList());
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        // A department appears if it has budget lines, an owner, or a workflow record.
        var codes = lines.Select(l => l.BrandCode).Concat(owners.Keys).Concat(map.Keys).Distinct()
            .Where(b => scope.Has(v.CompanyId, b)).OrderBy(b => b);

        var draft = v.Status == VersionStatus.Draft;
        return codes.Select(b =>
        {
            map.TryGetValue(b, out var st);
            var status = st?.Status ?? DeptStatus.Draft;
            var ls = lines.Where(l => l.BrandCode == b).ToList();
            var isApprover = scope.IsAdmin || (st?.ApproverId is int a && a == scope.User.Id);
            return new DeptView(b, brands.GetValueOrDefault(b, b), owners.GetValueOrDefault(b) ?? new(), status,
                st?.SubmittedById is int s ? names.GetValueOrDefault(s) : null, st?.SubmittedAt,
                st is null || status == DeptStatus.Draft ? null : st.ApproverId is int ap ? names.GetValueOrDefault(ap) : "Administrator",
                st?.DecidedById is int d ? names.GetValueOrDefault(d) : null, st?.DecidedAt, st?.Comment,
                ls.Count,
                ls.Where(l => AppDbContext.KindOf(kinds, l.AccountCode) == AccountKind.Revenue).Sum(l => l.Amounts.Sum()),
                ls.Where(l => AppDbContext.KindOf(kinds, l.AccountCode) == AccountKind.Expense).Sum(l => l.Amounts.Sum()),
                ls.Where(l => AppDbContext.KindOf(kinds, l.AccountCode) == AccountKind.Other).Sum(l => l.Amounts.Sum()),
                CanEdit: draft && Editable(status),
                CanSubmit: draft && Editable(status),
                // Admins may approve directly (e.g. departments with no owner in the app).
                CanApprove: draft && (status == DeptStatus.Submitted ? isApprover : scope.IsAdmin && Editable(status)),
                CanReject: draft && status == DeptStatus.Submitted && isApprover,
                CanReopen: draft && (status == DeptStatus.Approved ? isApprover
                    : status == DeptStatus.Submitted && (isApprover || st?.SubmittedById == scope.User.Id)));
        }).ToList();
    }

    public async Task ActAsync(BudgetVersion v, string brand, DeptAction action, string? comment, Scope scope)
    {
        if (v.Status != VersionStatus.Draft) throw new InvalidOperationException("This revision is already approved — create a revision to change it.");
        scope.Require(v.CompanyId, brand);
        var view = (await DepartmentsAsync(v, scope)).FirstOrDefault(d => d.Brand == brand)
                   ?? throw new KeyNotFoundException($"Department {brand} not found in this budget.");
        var allowed = action switch
        {
            DeptAction.Submitted => view.CanSubmit,
            DeptAction.Approved => view.CanApprove,
            DeptAction.Rejected => view.CanReject,
            DeptAction.Reopened => view.CanReopen,
            _ => false,
        };
        if (!allowed) throw new ForbiddenException($"You can't {Verb(action)} {brand} now (status: {view.Status}).");
        if (action == DeptAction.Rejected && string.IsNullOrWhiteSpace(comment))
            throw new InvalidOperationException("Say why it is rejected, so the owner knows what to change.");

        var st = await db.DepartmentStatuses.FirstOrDefaultAsync(d => d.VersionId == v.Id && d.BrandCode == brand);
        if (st is null) db.DepartmentStatuses.Add(st = new DepartmentStatus { VersionId = v.Id, BrandCode = brand });
        var now = DateTime.UtcNow;
        var me = scope.User;
        switch (action)
        {
            case DeptAction.Submitted:
                st.Status = DeptStatus.Submitted;
                st.SubmittedById = me.Id; st.SubmittedAt = now;
                st.ApproverId = me.ManagerId;           // null → any admin
                st.DecidedById = null; st.DecidedAt = null;
                break;
            case DeptAction.Approved:
                st.Status = DeptStatus.Approved;
                st.DecidedById = me.Id; st.DecidedAt = now;
                break;
            case DeptAction.Rejected:
                st.Status = DeptStatus.Rejected;
                st.DecidedById = me.Id; st.DecidedAt = now;
                break;
            case DeptAction.Reopened:
                st.Status = DeptStatus.Draft;
                st.DecidedById = null; st.DecidedAt = null;
                break;
        }
        st.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        db.DepartmentEvents.Add(new DepartmentEvent { VersionId = v.Id, BrandCode = brand, Action = action, UserId = me.Id, At = now, Comment = st.Comment });
        await db.SaveChangesAsync();

        // Notify after the change is saved; a mail problem must never undo or block the approval step.
        try { await notifier.DepartmentActionAsync(v, brand, action, me, st); }
        catch (Exception ex) { log.LogWarning(ex, "Could not queue notification for {Brand}", brand); }
    }

    private static string Verb(DeptAction a) => a switch
    {
        DeptAction.Submitted => "submit", DeptAction.Approved => "approve", DeptAction.Rejected => "reject", _ => "reopen",
    };

    /// <summary>Brands with budget lines that are not approved yet — blocks approving the whole revision.</summary>
    public async Task<List<string>> PendingBrandsAsync(BudgetVersion v)
    {
        var map = await StatusesAsync(v.Id);
        var withLines = await db.Lines.Where(l => l.VersionId == v.Id).Select(l => l.BrandCode).Distinct().ToListAsync();
        return withLines.Where(b => StatusOf(map, b) != DeptStatus.Approved).OrderBy(b => b).ToList();
    }

    /// <summary>A new revision starts with every department approved as it was (the admin reopens the ones that must change).</summary>
    public async Task CarryOverAsync(BudgetVersion src, BudgetVersion rev, int userId)
    {
        var brands = await db.Lines.Where(l => l.VersionId == src.Id).Select(l => l.BrandCode).Distinct().ToListAsync();
        var old = await StatusesAsync(src.Id);
        var now = DateTime.UtcNow;
        foreach (var b in brands)
        {
            old.TryGetValue(b, out var o);
            db.DepartmentStatuses.Add(new DepartmentStatus
            {
                VersionId = rev.Id, BrandCode = b, Status = DeptStatus.Approved,
                SubmittedById = o?.SubmittedById, SubmittedAt = o?.SubmittedAt, ApproverId = o?.ApproverId,
                DecidedById = o?.DecidedById ?? userId, DecidedAt = o?.DecidedAt ?? now,
                Comment = $"Carried over from Rev {src.RevisionNo}",
            });
        }
        await db.SaveChangesAsync();
    }
}
