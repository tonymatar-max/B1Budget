using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace B1Budget.Api.Services;

public record BvaRow(string Brand, string BrandName, string Account, string AccountName, AccountKind Kind,
    decimal[] Budget, decimal[] Actual, decimal[]? Compare);

public record BvaReport(int VersionId, string VersionName, int FiscalYear, int FromPeriod, int ToPeriod,
    string[] PeriodLabels, int CurrentPeriod, DateTime ActualsAsOf, List<BvaRow> Rows,
    int? CompareVersionId, string? CompareVersionName);

public record GroupCompanyPart(int CompanyId, decimal[] Budget, decimal[] Actual);
public record GroupRow(string Brand, string BrandName, string Account, string AccountName, AccountKind Kind,
    decimal[] Budget, decimal[] Actual, List<GroupCompanyPart> ByCompany);
public record GroupCompany(int Id, string Name, string Currency, decimal GroupRate, int? VersionId, string? VersionLabel,
    List<VersionOption> Options, string? Error);
public record VersionOption(int Id, string Label);
public record GroupReport(int FiscalYear, int FromPeriod, int ToPeriod, string[] PeriodLabels, int CurrentPeriod,
    List<GroupCompany> Companies, List<GroupRow> Rows);

/// <summary>
/// Budget vs actual: budget from the local version, actuals from JDT1 grouped by dimension + account + fiscal period.
/// Both sides are "natural sign": revenue = credit − debit, expense = debit − credit.
/// </summary>
public class ReportService(AppDbContext db, GatewayFactory factory, IMemoryCache cache)
{
    public async Task<BvaReport> BudgetVsActualAsync(int versionId, int? fromPeriod, int? toPeriod, bool refresh, int? compareVersionId, CancellationToken ct)
    {
        var version = await db.Versions.Include(v => v.Lines).AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw new KeyNotFoundException("Budget version not found.");
        var compare = compareVersionId is int cid && cid != versionId
            ? await db.Versions.Include(v => v.Lines).AsNoTracking().FirstOrDefaultAsync(v => v.Id == cid, ct)
              ?? throw new KeyNotFoundException("Comparison version not found.")
            : null;
        if (compare != null && compare.CompanyId != version.CompanyId)
            throw new InvalidOperationException("The second budget must belong to the same company — use the group report to combine companies.");
        var settings = await db.Companies.FirstAsync(c => c.Id == version.CompanyId, ct);
        using var gateway = factory.Create(settings);
        var cal = new FiscalCalendar(settings.FiscalYearStartMonth);
        var year = version.FiscalYear;

        var today = DateTime.UtcNow.Date;
        var currentPeriod = cal.PeriodOf(year, today);
        if (currentPeriod == 0) currentPeriod = today < cal.YearStart(year) ? 0 : 12;
        var from = Math.Clamp(fromPeriod ?? 1, 1, 12);
        var to = Math.Clamp(toPeriod ?? (currentPeriod == 0 ? 12 : currentPeriod), from, 12);

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == settings.Id).ToDictionaryAsync(a => a.Code, ct);
        var brands = await db.Brands.AsNoTracking().Where(b => b.CompanyId == settings.Id).ToDictionaryAsync(b => b.Code, ct);

        // Actuals for the whole fiscal year (cached briefly) — period filtering is applied client-side on totals.
        var (actualRows, asOf) = await CachedActualsAsync(settings, gateway, year, refresh, ct);

        var map = new Dictionary<(string Brand, string Account), (decimal[] Budget, decimal[] Actual, decimal[] Compare)>();
        (decimal[] Budget, decimal[] Actual, decimal[] Compare) Get(string b, string a)
        {
            if (!map.TryGetValue((b, a), out var v)) map[(b, a)] = v = (new decimal[12], new decimal[12], new decimal[12]);
            return v;
        }

        foreach (var l in version.Lines)
        {
            var slot = Get(l.BrandCode, l.AccountCode).Budget;
            for (var i = 0; i < 12; i++) slot[i] += l.Amounts[i];
        }

        foreach (var l in compare?.Lines ?? [])
        {
            var slot = Get(l.BrandCode, l.AccountCode).Compare;
            for (var i = 0; i < 12; i++) slot[i] += l.Amounts[i];
        }

        foreach (var r in actualRows)
        {
            if (!accounts.TryGetValue(r.Account, out var acc)) continue;     // balance-sheet account
            var p = cal.PeriodOf(year, r.Date);
            if (p == 0) continue;
            var amount = acc.Kind == AccountKind.Revenue ? r.Credit - r.Debit : r.Debit - r.Credit;
            Get(r.Brand ?? "", r.Account).Actual[p - 1] += amount;
        }

        var result = map
            .Where(kv => kv.Value.Budget.Any(x => x != 0) || kv.Value.Actual.Any(x => x != 0) || kv.Value.Compare.Any(x => x != 0))
            .Select(kv =>
            {
                var acc = accounts.GetValueOrDefault(kv.Key.Account);
                var brandName = kv.Key.Brand == "" ? "(no brand)" : brands.GetValueOrDefault(kv.Key.Brand)?.Name ?? kv.Key.Brand;
                return new BvaRow(kv.Key.Brand, brandName, kv.Key.Account, acc?.Name ?? kv.Key.Account,
                    acc?.Kind ?? AccountKind.Expense, kv.Value.Budget, kv.Value.Actual, compare is null ? null : kv.Value.Compare);
            })
            .OrderBy(r => r.Brand == "" ? 1 : 0).ThenBy(r => r.Brand).ThenBy(r => r.Kind).ThenBy(r => r.Account)
            .ToList();

        var labels = Enumerable.Range(1, 12).Select(p => cal.PeriodLabel(year, p)).ToArray();
        return new BvaReport(version.Id, $"{version.Name} · Rev {version.RevisionNo}", year, from, to, labels, currentPeriod, asOf, result,
            compare?.Id, compare is null ? null : $"{compare.Name} · Rev {compare.RevisionNo}");
    }

    /// <summary>
    /// Group budget vs actual for one fiscal year: every company's chosen revision (default: the most recently
    /// approved one) and its actuals, converted with the company's group rate and summed by brand code + account code.
    /// A company that can't be reached is reported with its error instead of failing the whole report.
    /// </summary>
    public async Task<GroupReport> GroupAsync(int year, int? fromPeriod, int? toPeriod, bool refresh,
        IReadOnlyDictionary<int, int> versionOverrides, CancellationToken ct)
    {
        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        if (companies.Count == 0) throw new InvalidOperationException("No companies configured.");

        // Periods line up by position; label them by month only if every company starts its year in the same month.
        var sameStart = companies.Select(c => c.FiscalYearStartMonth).Distinct().Count() == 1;
        var cal0 = new FiscalCalendar(companies[0].FiscalYearStartMonth);
        var labels = Enumerable.Range(1, 12).Select(p => sameStart ? cal0.PeriodLabel(year, p) : $"P{p}").ToArray();
        var today = DateTime.UtcNow.Date;
        var current = cal0.PeriodOf(year, today);
        if (current == 0) current = today < cal0.YearStart(year) ? 0 : 12;
        var from = Math.Clamp(fromPeriod ?? 1, 1, 12);
        var to = Math.Clamp(toPeriod ?? (current == 0 ? 12 : current), from, 12);

        var rows = new Dictionary<(string Brand, string Account), GroupRow>();
        var parts = new List<GroupCompany>();
        foreach (var c in companies)
        {
            var candidates = await db.Versions.AsNoTracking()
                .Where(v => v.CompanyId == c.Id && v.FiscalYear == year && v.Status != VersionStatus.Draft)
                .OrderByDescending(v => v.ApprovedAt).ToListAsync(ct);
            var options = candidates.Select(v => new VersionOption(v.Id, $"{v.Name} · Rev {v.RevisionNo}{(v.Status == VersionStatus.Superseded ? " (superseded)" : "")}")).ToList();
            var chosen = versionOverrides.TryGetValue(c.Id, out var vid) ? candidates.FirstOrDefault(v => v.Id == vid) : null;
            chosen ??= candidates.FirstOrDefault(v => v.Status != VersionStatus.Superseded) ?? candidates.FirstOrDefault();

            string? error = null;
            try
            {
                var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == c.Id).ToDictionaryAsync(a => a.Code, ct);
                var brands = await db.Brands.AsNoTracking().Where(b => b.CompanyId == c.Id).ToDictionaryAsync(b => b.Code, ct);
                var rate = c.GroupRate == 0 ? 1 : c.GroupRate;
                var cal = new FiscalCalendar(c.FiscalYearStartMonth);

                GroupCompanyPart Part(string brand, string account, AccountKind kind)
                {
                    if (!rows.TryGetValue((brand, account), out var row))
                        rows[(brand, account)] = row = new GroupRow(brand,
                            brand == "" ? "(no brand)" : brands.GetValueOrDefault(brand)?.Name ?? brand,
                            account, accounts.GetValueOrDefault(account)?.Name ?? account, kind, new decimal[12], new decimal[12], new());
                    var part = row.ByCompany.FirstOrDefault(p => p.CompanyId == c.Id);
                    if (part is null) row.ByCompany.Add(part = new GroupCompanyPart(c.Id, new decimal[12], new decimal[12]));
                    return part;
                }

                if (chosen != null)
                    foreach (var l in await db.Lines.AsNoTracking().Where(l => l.VersionId == chosen.Id).ToListAsync(ct))
                    {
                        var part = Part(l.BrandCode, l.AccountCode, accounts.GetValueOrDefault(l.AccountCode)?.Kind ?? AccountKind.Expense);
                        for (var i = 0; i < 12; i++) part.Budget[i] += l.Amounts[i] * rate;
                    }

                using var gateway = factory.Create(c);
                var (actuals, _) = await CachedActualsAsync(c, gateway, year, refresh, ct);
                foreach (var r in actuals)
                {
                    if (!accounts.TryGetValue(r.Account, out var acc)) continue;
                    var p = cal.PeriodOf(year, r.Date);
                    if (p == 0) continue;
                    var amount = acc.Kind == AccountKind.Revenue ? r.Credit - r.Debit : r.Debit - r.Credit;
                    Part(r.Brand ?? "", r.Account, acc.Kind).Actual[p - 1] += amount * rate;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

            parts.Add(new GroupCompany(c.Id, c.Name, c.Currency, c.GroupRate, chosen?.Id,
                chosen is null ? null : $"{chosen.Name} · Rev {chosen.RevisionNo}", options, error));
        }

        foreach (var row in rows.Values)
            foreach (var part in row.ByCompany)
                for (var i = 0; i < 12; i++) { row.Budget[i] += part.Budget[i]; row.Actual[i] += part.Actual[i]; }

        var result = rows.Values
            .Where(r => r.Budget.Any(x => x != 0) || r.Actual.Any(x => x != 0))
            .OrderBy(r => r.Brand == "" ? 1 : 0).ThenBy(r => r.Brand).ThenBy(r => r.Kind).ThenBy(r => r.Account)
            .ToList();
        return new GroupReport(year, from, to, labels, current, parts, result);
    }

    /// <summary>A company's JDT1 actuals for a whole fiscal year, cached for 5 minutes.</summary>
    private async Task<(List<ActualRow> Rows, DateTime AsOf)> CachedActualsAsync(Company c, IB1Gateway gateway, int year, bool refresh, CancellationToken ct)
    {
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var key = $"act:{c.Id}:{c.Mode}:{c.CompanyDb}:{c.Dimension}:{year}:{c.FiscalYearStartMonth}";
        if (refresh) cache.Remove(key);
        return await cache.GetOrCreateAsync(key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var rows = await gateway.GetActualsAsync(c.Dimension, cal.YearStart(year), cal.PeriodEnd(year, 12), ct);
            return (rows, DateTime.UtcNow);
        });
    }

    public async Task<List<JournalLineDto>> DrillAsync(int versionId, string brand, string account, int fromPeriod, int toPeriod, CancellationToken ct)
    {
        var version = await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw new KeyNotFoundException("Budget version not found.");
        var settings = await db.Companies.FirstAsync(c => c.Id == version.CompanyId, ct);
        using var gateway = factory.Create(settings);
        var cal = new FiscalCalendar(settings.FiscalYearStartMonth);
        return await gateway.GetJournalLinesAsync(settings.Dimension, account, brand,
            cal.PeriodStart(version.FiscalYear, Math.Clamp(fromPeriod, 1, 12)),
            cal.PeriodEnd(version.FiscalYear, Math.Clamp(toPeriod, 1, 12)), ct);
    }

    /// <summary>Per-period actuals for a fiscal year keyed by (brand, account) — used to seed a budget from history.</summary>
    public async Task<Dictionary<(string Brand, string Account), decimal[]>> ActualsByKeyAsync(Company settings, int fiscalYear, CancellationToken ct)
    {
        using var gateway = factory.Create(settings);
        var cal = new FiscalCalendar(settings.FiscalYearStartMonth);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == settings.Id).ToDictionaryAsync(a => a.Code, ct);
        var rows = await gateway.GetActualsAsync(settings.Dimension, cal.YearStart(fiscalYear), cal.PeriodEnd(fiscalYear, 12), ct);
        var result = new Dictionary<(string, string), decimal[]>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Brand) || !accounts.TryGetValue(r.Account, out var acc)) continue;
            var p = cal.PeriodOf(fiscalYear, r.Date);
            if (p == 0) continue;
            if (!result.TryGetValue((r.Brand, r.Account), out var arr)) result[(r.Brand, r.Account)] = arr = new decimal[12];
            arr[p - 1] += acc.Kind == AccountKind.Revenue ? r.Credit - r.Debit : r.Debit - r.Credit;
        }
        return result;
    }
}
