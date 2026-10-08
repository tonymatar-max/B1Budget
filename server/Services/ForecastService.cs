using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public record ForecastBrandRow(string Brand, string BrandName, bool CanEdit,
    decimal[] Forecast, decimal[] Budget, decimal[] Actual);

/// <summary>Projects the not-yet-elapsed months of one member from its current-year actuals, last year's actuals
/// and (dimension basis) its budget. Elapsed months are always the real actuals.</summary>
public static class ForecastMath
{
    public static decimal[] Project(decimal[] cur, decimal[] prior, decimal[] budget, int elapsed, ForecastMethod method, decimal growthPct)
    {
        var g = growthPct / 100m;
        var res = new decimal[12];
        for (var i = 0; i < Math.Min(elapsed, 12); i++) res[i] = cur[i];

        decimal ytd = 0; for (var i = 0; i < Math.Min(elapsed, 12); i++) ytd += cur[i];
        var runRate = elapsed > 0 ? ytd / elapsed : 0m;
        decimal priorYtd = 0; for (var i = 0; i < Math.Min(elapsed, 12); i++) priorYtd += prior[i];
        var priorSum = prior.Sum();
        var priorAvg = priorSum / 12m;
        var pace = priorYtd != 0 ? ytd / priorYtd : 0m;

        // Straight-line regression through the elapsed actual points (x = 1..elapsed): y = slope·x + intercept.
        decimal slope = 0, intercept = 0;
        if (elapsed >= 2)
        {
            decimal n = elapsed, sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (var i = 0; i < elapsed; i++) { decimal x = i + 1, y = cur[i]; sx += x; sy += y; sxx += x * x; sxy += x * y; }
            var denom = n * sxx - sx * sx;
            slope = denom != 0 ? (n * sxy - sx * sy) / denom : 0m;
            intercept = (sy - slope * sx) / n;
        }

        // When last year is empty, prior-based methods fall back to the run-rate (or a grown prior average when nothing elapsed).
        var fallback = runRate > 0 ? runRate : priorAvg * (1 + g);

        for (var i = elapsed; i < 12; i++)
        {
            var v = method switch
            {
                ForecastMethod.Budget => budget[i],
                ForecastMethod.RunRate => runRate > 0 ? runRate : fallback,
                ForecastMethod.PriorYearGrowth => priorSum != 0 ? prior[i] * (1 + g) : fallback,
                ForecastMethod.SeasonalRunRate => priorYtd != 0 ? prior[i] * pace : (priorSum != 0 ? prior[i] * (1 + g) : fallback),
                ForecastMethod.LinearTrend => elapsed >= 2 ? Math.Max(0m, slope * (i + 1) + intercept) : fallback,
                _ => 0m,
            };
            res[i] = Math.Round(v, 2);
        }
        return res;
    }
}

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

    /// <summary>Actual sales per member for a year (no budget/names) — used for the prior-year baseline of a projection.</summary>
    private async Task<Dictionary<string, decimal[]>> ActualsByMemberAsync(Company c, ForecastBasis basis, string udf, int year, bool refresh, CancellationToken ct)
    {
        var actual = new Dictionary<string, decimal[]>();
        try
        {
            if (basis == ForecastBasis.Dimension) { var (a, _) = await reports.SalesActualsByBrandAsync(c, year, refresh, ct); actual = a; }
            else { var (byM, _) = await reports.SalesByMemberAsync(c, basis, udf, year, refresh, ct); foreach (var (code, (_, amt)) in byM) actual[code] = amt; }
        }
        catch { /* prior year is best-effort */ }
        return actual;
    }

    /// <summary>Per-month average of the previous <paramref name="years"/> years' actual sales per member
    /// (year-1 … year-N). A member's months are averaged only over the prior years in which it actually sold.</summary>
    private async Task<Dictionary<string, decimal[]>> PriorYearsAverageAsync(Company c, ForecastBasis basis, string udf, int year, int years, bool refresh, CancellationToken ct)
    {
        var perYear = new List<Dictionary<string, decimal[]>>();
        for (var k = 1; k <= years; k++) perYear.Add(await ActualsByMemberAsync(c, basis, udf, year - k, refresh, ct));
        var avg = new Dictionary<string, decimal[]>();
        foreach (var code in perYear.SelectMany(d => d.Keys).Distinct())
        {
            var arr = new decimal[12];
            var yearsWith = 0;
            foreach (var yd in perYear)
                if (yd.TryGetValue(code, out var a) && a.Any(v => v != 0)) { yearsWith++; for (var i = 0; i < 12; i++) arr[i] += a[i]; }
            if (yearsWith > 0) { for (var i = 0; i < 12; i++) arr[i] = Math.Round(arr[i] / yearsWith, 2); avg[code] = arr; }
        }
        return avg;
    }

    /// <summary>
    /// Seed editable members with a forecasting <paramref name="method"/>: elapsed months are always the real
    /// actuals; the remaining months are projected (budget, run-rate, prior-year growth, seasonal, linear trend,
    /// or the average of the previous <paramref name="years"/> years). Overwrites those members; returns those written.
    /// </summary>
    public async Task<List<string>> SeedAsync(Company c, int year, ForecastBasis basis, string? udf, ForecastMethod method, decimal growthPct, int years, Scope scope, bool refresh, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        udf ??= "";
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var elapsed = ElapsedMonths(cal, year, DateTime.UtcNow.Date);
        var (names, budget, actual, _, _, _) = await LoadBasisAsync(c, basis, udf, year, refresh, ct);
        // Budget projection only makes sense on the dimension basis; elsewhere fall back to run-rate.
        if (method == ForecastMethod.Budget && basis != ForecastBasis.Dimension) method = ForecastMethod.RunRate;

        // Prior-year baseline: the average of the previous X years, or just last year, per the method.
        var prior = new Dictionary<string, decimal[]>();
        var projMethod = method;
        if (method == ForecastMethod.PriorYearsAverage)
        {
            prior = await PriorYearsAverageAsync(c, basis, udf, year, Math.Clamp(years, 1, 10), refresh, ct);
            projMethod = ForecastMethod.PriorYearGrowth;   // project the averaged array exactly like prior-year growth
        }
        else if (method is ForecastMethod.PriorYearGrowth or ForecastMethod.SeasonalRunRate)
            prior = await ActualsByMemberAsync(c, basis, udf, year - 1, refresh, ct);

        bool CanEdit(string code) => basis != ForecastBasis.Dimension ? scope.IsAdmin : scope.Has(c.Id, code);
        var members = names.Keys.Concat(budget.Keys).Concat(actual.Keys).Concat(prior.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && CanEdit(code)).Distinct().ToList();

        var f = await EnsureForecastAsync(c.Id, year, basis, udf, ct);
        await db.SaveChangesAsync(ct);
        var existing = await db.ForecastLines.Where(l => l.ForecastId == f.Id).ToDictionaryAsync(l => l.BrandCode, ct);
        var seeded = new List<string>();
        foreach (var code in members)
        {
            var amounts = ForecastMath.Project(
                actual.GetValueOrDefault(code) ?? new decimal[12],
                prior.GetValueOrDefault(code) ?? new decimal[12],
                budget.GetValueOrDefault(code) ?? new decimal[12],
                elapsed, projMethod, growthPct);
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
