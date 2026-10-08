using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public record ForecastBrandRow(string Brand, string BrandName, bool CanEdit,
    decimal[] Forecast, decimal[] Budget, decimal[] Actual);

public record ForecastReport(int CompanyId, string Currency, int FiscalYear, int[] AvailableYears,
    ForecastBasis Basis, string Udf, string[] UdfFields, bool HasBudgetBaseline, string MemberLabel,
    string[] PeriodLabels, int CurrentPeriod, int ElapsedMonths, bool HasForecast, DateTime? UpdatedAt,
    DateTime? ActualsAsOf, int? BudgetVersionId, string? BudgetVersionName, List<ForecastBrandRow> Rows,
    string? ActualsError);

/// <summary>
/// Sales forecast for one company, fiscal year and <see cref="ForecastBasis"/>: expected sales per member per
/// month. A member is a cost center (dimension), item group, item, or item-UDF value. Actual sales come from
/// journal lines (dimension) or sales invoices (item bases). The sales budget baseline only exists for the
/// dimension basis (the app budgets by cost center × account); item bases seed the future from a run-rate.
/// App-only — nothing here is pushed to SAP B1.
/// </summary>
public class ForecastService(AppDbContext db, ReportService reports, GatewayFactory factory)
{
    /// <summary>What a member is called, for UI labels.</summary>
    public static string MemberLabelFor(ForecastBasis basis, string udf) => basis switch
    {
        ForecastBasis.ItemGroup => "Item group",
        ForecastBasis.Item => "Item",
        ForecastBasis.ItemUdf => string.IsNullOrWhiteSpace(udf) ? "Item UDF" : udf,
        _ => "Cost center",
    };

    /// <summary>Item-based bases are not department-scoped, so only administrators may view or edit them.</summary>
    public static void RequireBasisAccess(ForecastBasis basis, Scope scope)
    {
        if (basis != ForecastBasis.Dimension && !scope.IsAdmin)
            throw new ForbiddenException("Only an administrator can work with item, item-group or UDF forecasts.");
    }

    private async Task<BudgetVersion?> BaselineVersionAsync(int companyId, int year, CancellationToken ct)
    {
        var candidates = await db.Versions.AsNoTracking().Where(v => v.CompanyId == companyId && v.FiscalYear == year).ToListAsync(ct);
        return candidates.Where(v => v.Status != VersionStatus.Draft).OrderByDescending(v => v.ApprovedAt).FirstOrDefault(v => v.Status != VersionStatus.Superseded)
            ?? candidates.Where(v => v.Status != VersionStatus.Draft).OrderByDescending(v => v.ApprovedAt).FirstOrDefault()
            ?? candidates.OrderByDescending(v => v.UpdatedAt).FirstOrDefault();
    }

    private async Task<Dictionary<string, decimal[]>> BudgetSalesByBrandAsync(Company c, BudgetVersion? baseline, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal[]>();
        if (baseline is null) return result;
        var kinds = await db.AccountKindsAsync(c.Id);
        foreach (var l in await db.Lines.AsNoTracking().Where(l => l.VersionId == baseline.Id).ToListAsync(ct))
        {
            if (AppDbContext.KindOf(kinds, l.AccountCode) != AccountKind.Revenue) continue;
            if (!result.TryGetValue(l.BrandCode, out var arr)) result[l.BrandCode] = arr = new decimal[12];
            for (var i = 0; i < 12; i++) arr[i] += l.Amounts[i];
        }
        return result;
    }

    public static int ElapsedMonths(FiscalCalendar cal, int year, DateTime today)
    {
        if (today > cal.PeriodEnd(year, 12)) return 12;
        var cur = cal.PeriodOf(year, today);
        return cur == 0 ? 0 : cur - 1;
    }

    /// <summary>Members + names, sales budget (dimension only) and actuals for a basis — the shared basis-specific load.
    /// <paramref name="Error"/> is a human-readable reason actuals could not be read (surfaced to the user), not thrown.</summary>
    private async Task<(Dictionary<string, string> Names, Dictionary<string, decimal[]> Budget, Dictionary<string, decimal[]> Actual,
        DateTime? AsOf, BudgetVersion? Baseline, string? Error)> LoadBasisAsync(Company c, ForecastBasis basis, string udf, int year, bool refresh, CancellationToken ct)
    {
        var names = new Dictionary<string, string>();
        var budget = new Dictionary<string, decimal[]>();
        var actual = new Dictionary<string, decimal[]>();
        DateTime? asOf = null;
        BudgetVersion? baseline = null;
        string? error = null;

        // An item-UDF forecast needs a field chosen; without one there is nothing to load (the UI picks one and reloads).
        if (basis == ForecastBasis.ItemUdf && string.IsNullOrWhiteSpace(udf))
            return (names, budget, actual, asOf, baseline, error);

        if (basis == ForecastBasis.Dimension)
        {
            foreach (var b in await db.Brands.AsNoTracking().Where(b => b.CompanyId == c.Id).ToListAsync(ct)) names[b.Code] = b.Name;
            baseline = await BaselineVersionAsync(c.Id, year, ct);
            budget = await BudgetSalesByBrandAsync(c, baseline, ct);
            try { (var a, var t) = await reports.SalesActualsByBrandAsync(c, year, refresh, ct); actual = a; asOf = t; }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        }
        else
        {
            // Selectable universe (groups / UDF values); Item basis derives members from sales.
            try
            {
                using var gw = factory.Create(c);
                foreach (var m in await gw.GetForecastMembersAsync(basis, udf, ct)) names.TryAdd(m.Code, m.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
            try
            {
                var (byMember, t) = await reports.SalesByMemberAsync(c, basis, udf, year, refresh, ct);
                asOf = t;
                foreach (var (code, (name, amounts)) in byMember) { actual[code] = amounts; if (!string.IsNullOrEmpty(name)) names[code] = name; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        }
        return (names, budget, actual, asOf, baseline, error);
    }

    public async Task<ForecastReport> GetAsync(Company c, int? year, ForecastBasis basis, string? udf, bool refresh, Scope scope, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        udf ??= "";
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var today = DateTime.UtcNow.Date;

        var budgetYears = await db.Versions.AsNoTracking().Where(v => v.CompanyId == c.Id).Select(v => v.FiscalYear).Distinct().ToListAsync(ct);
        var forecastYears = await db.Forecasts.AsNoTracking().Where(f => f.CompanyId == c.Id).Select(f => f.FiscalYear).ToListAsync(ct);
        // Offer a few recent years too, so item-based forecasting on a live company can reach years that have sales
        // but no budget yet. Any explicitly requested year is always included.
        var current = cal.FiscalYearOf(today);
        var recent = Enumerable.Range(current - 4, 7);   // current-4 … current+2
        var years = budgetYears.Concat(forecastYears).Concat(recent).Append(current)
            .Concat(year is int y0 ? [y0] : Array.Empty<int>())
            .Distinct().OrderByDescending(y => y).ToArray();
        var fy = year ?? (years.Contains(current) ? current : years.First());

        var udfFields = new List<string>();
        if (basis == ForecastBasis.ItemUdf)
            try { using var gw = factory.Create(c); udfFields = await gw.GetItemUdfFieldsAsync(ct); } catch { /* best-effort */ }

        var (names, budget, actual, asOf, baseline, error) = await LoadBasisAsync(c, basis, udf, fy, refresh, ct);

        var forecast = await db.Forecasts.AsNoTracking().Include(f => f.Lines)
            .FirstOrDefaultAsync(f => f.CompanyId == c.Id && f.FiscalYear == fy && f.Basis == basis && f.UdfName == udf, ct);
        var forecastLines = forecast?.Lines.ToDictionary(l => l.BrandCode, l => l.Amounts) ?? new();

        // Who can edit which member: dimension is department-scoped; item bases are admin-only (already checked).
        bool CanEdit(string code) => basis != ForecastBasis.Dimension ? scope.IsAdmin : scope.Has(c.Id, code);

        var codes = names.Keys.Concat(budget.Keys).Concat(forecastLines.Keys).Concat(actual.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && CanEdit(code))
            .Distinct().OrderBy(code => code).ToList();

        var rows = codes.Select(code => new ForecastBrandRow(
            code, names.GetValueOrDefault(code, code), CanEdit(code),
            forecastLines.GetValueOrDefault(code) ?? new decimal[12],
            budget.GetValueOrDefault(code) ?? new decimal[12],
            actual.GetValueOrDefault(code) ?? new decimal[12])).ToList();

        var current2 = today > cal.PeriodEnd(fy, 12) ? 13 : cal.PeriodOf(fy, today);
        var labels = Enumerable.Range(1, 12).Select(p => cal.PeriodLabel(fy, p)).ToArray();
        return new ForecastReport(c.Id, c.Currency, fy, years, basis, udf, udfFields.ToArray(),
            basis == ForecastBasis.Dimension && baseline != null, MemberLabelFor(basis, udf),
            labels, current2, ElapsedMonths(cal, fy, today), forecast != null, forecast?.UpdatedAt, asOf,
            baseline?.Id, baseline is null ? null : $"{baseline.Name} · Rev {baseline.RevisionNo}", rows, error);
    }

    private async Task<SalesForecast> EnsureForecastAsync(int companyId, int year, ForecastBasis basis, string udf, CancellationToken ct)
    {
        var f = await db.Forecasts.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.FiscalYear == year && x.Basis == basis && x.UdfName == udf, ct);
        if (f is null) db.Forecasts.Add(f = new SalesForecast { CompanyId = companyId, FiscalYear = year, Basis = basis, UdfName = udf });
        return f;
    }

    public async Task SaveMemberAsync(Company c, int year, ForecastBasis basis, string? udf, string member, decimal[] amounts, Scope scope, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        udf ??= "";
        if (amounts.Length != 12) throw new InvalidOperationException("A forecast line needs exactly 12 monthly amounts.");
        if (basis == ForecastBasis.Dimension) scope.Require(c.Id, member);
        var f = await EnsureForecastAsync(c.Id, year, basis, udf, ct);
        await db.SaveChangesAsync(ct);
        var line = await db.ForecastLines.FirstOrDefaultAsync(l => l.ForecastId == f.Id && l.BrandCode == member, ct);
        if (line is null) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = member });
        line.Amounts = amounts.ToArray();
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seed editable members: elapsed months from actual sales; remaining months from the sales budget
    /// (dimension basis) or the elapsed-month run-rate (item bases, which have no budget). Overwrites those members.
    /// </summary>
    public async Task<List<string>> SeedAsync(Company c, int year, ForecastBasis basis, string? udf, Scope scope, bool refresh, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        udf ??= "";
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var elapsed = ElapsedMonths(cal, year, DateTime.UtcNow.Date);
        var (names, budget, actual, _, _, _) = await LoadBasisAsync(c, basis, udf, year, refresh, ct);

        bool CanEdit(string code) => basis != ForecastBasis.Dimension ? scope.IsAdmin : scope.Has(c.Id, code);
        var members = names.Keys.Concat(budget.Keys).Concat(actual.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && CanEdit(code)).Distinct().ToList();

        var f = await EnsureForecastAsync(c.Id, year, basis, udf, ct);
        await db.SaveChangesAsync(ct);
        var existing = await db.ForecastLines.Where(l => l.ForecastId == f.Id).ToDictionaryAsync(l => l.BrandCode, ct);
        var seeded = new List<string>();
        foreach (var code in members)
        {
            var b = budget.GetValueOrDefault(code) ?? new decimal[12];
            var a = actual.GetValueOrDefault(code) ?? new decimal[12];
            // Future fill: budget for the dimension basis; otherwise the average of the elapsed actual months.
            decimal runRate = 0;
            if (basis != ForecastBasis.Dimension && elapsed > 0)
            {
                decimal s = 0; for (var i = 0; i < elapsed; i++) s += a[i];
                runRate = Math.Round(s / elapsed, 2);
            }
            var amounts = new decimal[12];
            for (var i = 0; i < 12; i++) amounts[i] = i < elapsed ? a[i] : (basis == ForecastBasis.Dimension ? b[i] : runRate);
            if (amounts.All(x => x == 0) && !existing.ContainsKey(code)) continue;
            if (!existing.TryGetValue(code, out var line)) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = code });
            line.Amounts = amounts;
            seeded.Add(code);
        }
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return seeded;
    }
}
