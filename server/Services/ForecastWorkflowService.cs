using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

/// <summary>What the signed-in user sees and may do on one approval unit of a forecast.</summary>
public record ForecastFlow(DeptStatus Status, string? SubmittedBy, DateTime? SubmittedAt, string? Approver, string? DecidedBy,
    DateTime? DecidedAt, string? Comment, bool CanSubmit, bool CanApprove, bool CanReject, bool CanReopen);

public record ForecastInboxItem(int ForecastId, int Year, ForecastBasis Basis, string Udf, ForecastMeasure Measure, string Member,
    string MemberName, int CompanyId, string CompanyName, string? SubmittedBy, DateTime? SubmittedAt, string? Comment, bool AssignedToMe);

/// <summary>
/// Approval workflow for sales forecasts, with the same rules as the budget's department workflow:
/// Draft -> Submitted (by the owner) -> Approved / Rejected (by the submitter's manager, or any admin; admins may also approve
/// directly). Rejected is editable again; Submitted and Approved are locked until an approver or admin reopens them.
/// The approval unit is one cost center on the cost-center basis, and the whole forecast ("*") on item-based bases.
/// </summary>
public class ForecastWorkflowService(AppDbContext db, Notifier notifier, ILogger<ForecastWorkflowService> log)
{
    /// <summary>MemberCode of the single approval unit of an item-based forecast.</summary>
    public const string All = "*";

    public static string UnitOf(ForecastBasis basis, string member) => basis == ForecastBasis.Dimension ? member : All;
    public static bool Editable(DeptStatus s) => s is DeptStatus.Draft or DeptStatus.Rejected;

    public Task<SalesForecast?> FindAsync(int companyId, int year, ForecastBasis basis, string udf, ForecastMeasure measure) =>
        db.Forecasts.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == companyId && f.FiscalYear == year
            && f.Basis == basis && f.UdfName == udf && f.Measure == measure);

    public async Task<Dictionary<string, ForecastStatus>> StatusesAsync(int? forecastId) =>
        forecastId is int id ? await db.ForecastStatuses.Where(s => s.ForecastId == id).ToDictionaryAsync(s => s.MemberCode) : new();

    public static DeptStatus StatusOf(Dictionary<string, ForecastStatus> map, string unit) =>
        map.TryGetValue(unit, out var s) ? s.Status : DeptStatus.Draft;

    private static string Describe(ForecastBasis basis, string member) => basis == ForecastBasis.Dimension ? $"Cost center {member}" : "This forecast";

    /// <summary>Throw unless this member's approval unit is currently editable (Draft or Rejected).</summary>
    public async Task RequireEditableAsync(int companyId, int year, ForecastBasis basis, string udf, ForecastMeasure measure, string member)
    {
        var f = await FindAsync(companyId, year, basis, udf, measure);
        if (f is null) return;
        var st = StatusOf(await StatusesAsync(f.Id), UnitOf(basis, member));
        if (Editable(st)) return;
        var what = Describe(basis, member);
        throw new InvalidOperationException(st == DeptStatus.Submitted
            ? $"{what} is waiting for approval - it can't be changed until it is approved, rejected or reopened."
            : $"{what} is approved and locked - ask your approver or an administrator to reopen it.");
    }

    /// <summary>The user's view of each requested unit. <paramref name="hasAccess"/> says whether they may work on it at all.</summary>
    public async Task<Dictionary<string, ForecastFlow>> FlowsAsync(SalesForecast? f, Scope scope, IEnumerable<string> units, Func<string, bool> hasAccess)
    {
        var map = await StatusesAsync(f?.Id);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var result = new Dictionary<string, ForecastFlow>();
        foreach (var unit in units.Distinct())
        {
            map.TryGetValue(unit, out var st);
            var status = st?.Status ?? DeptStatus.Draft;
            var access = hasAccess(unit);
            var isApprover = access && (scope.IsAdmin || (st?.ApproverId is int a && a == scope.User.Id));
            result[unit] = new ForecastFlow(status,
                st?.SubmittedById is int s ? names.GetValueOrDefault(s) : null, st?.SubmittedAt,
                st is null || status == DeptStatus.Draft ? null : st.ApproverId is int ap ? names.GetValueOrDefault(ap) : "Administrator",
                st?.DecidedById is int d ? names.GetValueOrDefault(d) : null, st?.DecidedAt, st?.Comment,
                CanSubmit: access && Editable(status),
                // Admins may approve directly, e.g. a cost center with no owner in the app.
                CanApprove: access && (status == DeptStatus.Submitted ? isApprover : scope.IsAdmin && Editable(status)),
                CanReject: access && status == DeptStatus.Submitted && isApprover,
                CanReopen: access && (status == DeptStatus.Approved ? isApprover
                    : status == DeptStatus.Submitted && (isApprover || st?.SubmittedById == scope.User.Id)));
        }
        return result;
    }

    public async Task ActAsync(Company c, int year, ForecastBasis basis, string? udf, ForecastMeasure measure, string member,
        DeptAction action, string? comment, Scope scope, CancellationToken ct)
    {
        ForecastService.RequireBasisAccess(basis, scope);
        measure = ForecastService.NormMeasure(basis, measure);
        udf ??= "";
        if (basis == ForecastBasis.Dimension) scope.Require(c.Id, member);
        var unit = UnitOf(basis, member);

        var f = await db.Forecasts.FirstOrDefaultAsync(x => x.CompanyId == c.Id && x.FiscalYear == year && x.Basis == basis && x.UdfName == udf && x.Measure == measure, ct)
                ?? throw new InvalidOperationException("There is no saved forecast yet - enter and save figures first.");

        var flow = (await FlowsAsync(f, scope, [unit], _ => true))[unit];
        var allowed = action switch
        {
            DeptAction.Submitted => flow.CanSubmit, DeptAction.Approved => flow.CanApprove,
            DeptAction.Rejected => flow.CanReject, DeptAction.Reopened => flow.CanReopen, _ => false,
        };
        if (!allowed) throw new ForbiddenException($"You can't {Verb(action)} {Describe(basis, member).ToLowerInvariant()} now (status: {flow.Status}).");
        if (action == DeptAction.Rejected && string.IsNullOrWhiteSpace(comment))
            throw new InvalidOperationException("Say why it is rejected, so the owner knows what to change.");

        // Locking an empty forecast is meaningless: a submission (or a direct approval) needs saved figures in the unit.
        if (action == DeptAction.Submitted || (action == DeptAction.Approved && flow.Status != DeptStatus.Submitted))
        {
            var lines = await db.ForecastLines.AsNoTracking().Where(l => l.ForecastId == f.Id).ToListAsync(ct);
            var has = lines.Any(l => (unit == All || l.BrandCode == member) && l.Amounts.Any(v => v != 0));
            if (!has) throw new InvalidOperationException($"{Describe(basis, member)} has no saved forecast figures yet - enter and save them first.");
        }

        var st = await db.ForecastStatuses.FirstOrDefaultAsync(s => s.ForecastId == f.Id && s.MemberCode == unit, ct);
        if (st is null) db.ForecastStatuses.Add(st = new ForecastStatus { ForecastId = f.Id, MemberCode = unit });
        var now = DateTime.UtcNow;
        var me = scope.User;
        switch (action)
        {
            case DeptAction.Submitted:
                st.Status = DeptStatus.Submitted; st.SubmittedById = me.Id; st.SubmittedAt = now;
                // Cost centers go to the submitter's manager; item-based forecasts are admin-only, so any admin approves.
                st.ApproverId = basis == ForecastBasis.Dimension ? me.ManagerId : null;
                st.DecidedById = null; st.DecidedAt = null;
                break;
            case DeptAction.Approved: st.Status = DeptStatus.Approved; st.DecidedById = me.Id; st.DecidedAt = now; break;
            case DeptAction.Rejected: st.Status = DeptStatus.Rejected; st.DecidedById = me.Id; st.DecidedAt = now; break;
            case DeptAction.Reopened: st.Status = DeptStatus.Draft; st.DecidedById = null; st.DecidedAt = null; break;
        }
        st.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        db.ForecastEvents.Add(new ForecastEvent { ForecastId = f.Id, MemberCode = unit, Action = action, UserId = me.Id, At = now, Comment = st.Comment });
        await db.SaveChangesAsync(ct);

        // Notify after the change is saved; a mail problem must never undo or block the approval step.
        try { await notifier.ForecastActionAsync(f, c, unit, action, me, st); }
        catch (Exception ex) { log.LogWarning(ex, "Could not queue forecast notification for {Unit}", unit); }
    }

    private static string Verb(DeptAction a) => a switch
    {
        DeptAction.Submitted => "submit", DeptAction.Approved => "approve", DeptAction.Rejected => "reject", _ => "reopen",
    };

    public async Task<List<object>> EventsAsync(Company c, int year, ForecastBasis basis, string? udf, ForecastMeasure measure, string member, Scope scope)
    {
        ForecastService.RequireBasisAccess(basis, scope);
        measure = ForecastService.NormMeasure(basis, measure);
        if (basis == ForecastBasis.Dimension) scope.Require(c.Id, member);
        var f = await FindAsync(c.Id, year, basis, udf ?? "", measure);
        if (f is null) return new();
        var unit = UnitOf(basis, member);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return (await db.ForecastEvents.AsNoTracking().Where(e => e.ForecastId == f.Id && e.MemberCode == unit).OrderByDescending(e => e.At).ToListAsync())
            .Select(e => (object)new { e.Action, e.At, e.Comment, User = names.GetValueOrDefault(e.UserId, "?") }).ToList();
    }

    /// <summary>Submitted forecasts waiting for this user (admins: every one), across companies.</summary>
    public async Task<List<ForecastInboxItem>> InboxAsync(Scope scope)
    {
        var me = scope.User.Id;
        var rows = await (from s in db.ForecastStatuses
                          join f in db.Forecasts on s.ForecastId equals f.Id
                          join c in db.Companies on f.CompanyId equals c.Id
                          where s.Status == DeptStatus.Submitted && (scope.IsAdmin || s.ApproverId == me)
                          select new { s, f, CompanyName = c.Name }).ToListAsync();
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var brandNames = await db.Brands.AsNoTracking().ToListAsync();
        return rows.OrderBy(r => r.s.SubmittedAt).Select(r => new ForecastInboxItem(
            r.f.Id, r.f.FiscalYear, r.f.Basis, r.f.UdfName, r.f.Measure, r.s.MemberCode,
            r.s.MemberCode == All ? "Whole forecast" : brandNames.FirstOrDefault(b => b.CompanyId == r.f.CompanyId && b.Code == r.s.MemberCode)?.Name ?? r.s.MemberCode,
            r.f.CompanyId, r.CompanyName, r.s.SubmittedById is int sb ? names.GetValueOrDefault(sb) : null, r.s.SubmittedAt, r.s.Comment,
            r.s.ApproverId == me)).ToList();
    }
}
