using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public record ForecastBrandRow(string Brand, string BrandName, bool CanEdit,
    decimal[] Forecast, decimal[] Budget, decimal[] Actual);

public record ForecastReport(int CompanyId, string Currency, int FiscalYear, int[] AvailableYears,
    string[] PeriodLabels, int CurrentPeriod, int ElapsedMonths, bool HasForecast, DateTime? UpdatedAt,
    DateTime? ActualsAsOf, int? BudgetVersionId, string? BudgetVersionName, List<ForecastBrandRow> Rows);

/// <summary>
/// Sales forecast for one company and fiscal year: expected sales per brand (cost center) per month.
/// The sales <b>budget</b> baseline is the revenue lines of the year's current budget revision; <b>actual</b>
/// sales come from B1. Owners edit only their own brands. App-only — nothing here is pushed to SAP B1.
/// </summary>
public class ForecastService(AppDbContext db, ReportService reports)
{
    /// <summary>The budget revision a forecast baselines against: the latest non-superseded approved revision for the year, else the latest of any.</summary>
    private async Task<BudgetVersion?> BaselineVersionAsync(int companyId, int year, CancellationToken ct)
    {
        var candidates = await db.Versions.AsNoTracking()
            .Where(v => v.CompanyId == companyId && v.FiscalYear == year)
            .ToListAsync(ct);
        return candidates.Where(v => v.Status != VersionStatus.Draft).OrderByDescending(v => v.ApprovedAt).FirstOrDefault(v => v.Status != VersionStatus.Superseded)
            ?? candidates.Where(v => v.Status != VersionStatus.Draft).OrderByDescending(v => v.ApprovedAt).FirstOrDefault()
            ?? candidates.OrderByDescending(v => v.UpdatedAt).FirstOrDefault();
    }

    /// <summary>Sales budget (sum of revenue lines) per brand × period for the baseline revision.</summary>
    private async Task<Dictionary<string, decimal[]>> BudgetSalesByBrandAsync(Company c, BudgetVersion? baseline, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal[]>();
        if (baseline is null) return result;
        var kinds = await db.AccountKindsAsync(c.Id);
        var lines = await db.Lines.AsNoTracking().Where(l => l.VersionId == baseline.Id).ToListAsync(ct);
        foreach (var l in lines)
        {
            if (AppDbContext.KindOf(kinds, l.AccountCode) != AccountKind.Revenue) continue;
            if (!result.TryGetValue(l.BrandCode, out var arr)) result[l.BrandCode] = arr = new decimal[12];
            for (var i = 0; i < 12; i++) arr[i] += l.Amounts[i];
        }
        return result;
    }

    /// <summary>Completed months of the fiscal year as of today (0 before it starts, 12 once it has ended).</summary>
    public static int ElapsedMonths(FiscalCalendar cal, int year, DateTime today)
    {
        if (today > cal.PeriodEnd(year, 12)) return 12;
        var cur = cal.PeriodOf(year, today);
        return cur == 0 ? 0 : cur - 1;
    }

    public async Task<ForecastReport> GetAsync(Company c, int? year, bool refresh, Scope scope, CancellationToken ct)
    {
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var today = DateTime.UtcNow.Date;

        var budgetYears = await db.Versions.AsNoTracking().Where(v => v.CompanyId == c.Id).Select(v => v.FiscalYear).Distinct().ToListAsync(ct);
        var forecastYears = await db.Forecasts.AsNoTracking().Where(f => f.CompanyId == c.Id).Select(f => f.FiscalYear).ToListAsync(ct);
        var years = budgetYears.Concat(forecastYears).Append(cal.FiscalYearOf(today)).Distinct().OrderByDescending(y => y).ToArray();
        var fy = year ?? (years.Contains(cal.FiscalYearOf(today)) ? cal.FiscalYearOf(today) : years.First());

        var baseline = await BaselineVersionAsync(c.Id, fy, ct);
        var budget = await BudgetSalesByBrandAsync(c, baseline, ct);

        var forecast = await db.Forecasts.AsNoTracking().Include(f => f.Lines).FirstOrDefaultAsync(f => f.CompanyId == c.Id && f.FiscalYear == fy, ct);
        var forecastLines = forecast?.Lines.ToDictionary(l => l.BrandCode, l => l.Amounts) ?? new();

        Dictionary<string, decimal[]> actual = new();
        DateTime? asOf = null;
        try { (actual, var a) = await reports.SalesActualsByBrandAsync(c, fy, refresh, ct); asOf = a; }
        catch { /* actuals are reference-only; a B1 outage must not block forecasting */ }

        var brands = await db.Brands.AsNoTracking().Where(b => b.CompanyId == c.Id).ToListAsync(ct);
        var names = brands.ToDictionary(b => b.Code, b => b.Name);
        var codes = brands.Select(b => b.Code)
            .Concat(budget.Keys).Concat(forecastLines.Keys).Concat(actual.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && scope.Has(c.Id, code))
            .Distinct().OrderBy(code => code).ToList();

        var rows = codes.Select(code => new ForecastBrandRow(
            code, names.GetValueOrDefault(code, code), scope.Has(c.Id, code),
            forecastLines.GetValueOrDefault(code) ?? new decimal[12],
            budget.GetValueOrDefault(code) ?? new decimal[12],
            actual.GetValueOrDefault(code) ?? new decimal[12])).ToList();

        var current = today > cal.PeriodEnd(fy, 12) ? 13 : cal.PeriodOf(fy, today);
        var labels = Enumerable.Range(1, 12).Select(p => cal.PeriodLabel(fy, p)).ToArray();
        return new ForecastReport(c.Id, c.Currency, fy, years, labels, current, ElapsedMonths(cal, fy, today),
            forecast != null, forecast?.UpdatedAt, asOf, baseline?.Id,
            baseline is null ? null : $"{baseline.Name} · Rev {baseline.RevisionNo}", rows);
    }

    private async Task<SalesForecast> EnsureForecastAsync(int companyId, int year, CancellationToken ct)
    {
        var f = await db.Forecasts.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.FiscalYear == year, ct);
        if (f is null) db.Forecasts.Add(f = new SalesForecast { CompanyId = companyId, FiscalYear = year });
        return f;
    }

    /// <summary>Set one brand's 12 monthly forecast figures (caller must have checked scope).</summary>
    public async Task SaveBrandAsync(Company c, int year, string brand, decimal[] amounts, CancellationToken ct)
    {
        if (amounts.Length != 12) throw new InvalidOperationException("A forecast line needs exactly 12 monthly amounts.");
        var f = await EnsureForecastAsync(c.Id, year, ct);
        await db.SaveChangesAsync(ct);   // make sure the forecast has an Id before adding lines
        var line = await db.ForecastLines.FirstOrDefaultAsync(l => l.ForecastId == f.Id && l.BrandCode == brand, ct);
        if (line is null) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = brand });
        line.Amounts = amounts.ToArray();
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seed the editable brands: completed months from B1 actual sales, remaining months from the sales budget.
    /// Returns the brands that were written. Existing figures for those brands are overwritten.
    /// </summary>
    public async Task<List<string>> SeedAsync(Company c, int year, Scope scope, bool refresh, CancellationToken ct)
    {
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var elapsed = ElapsedMonths(cal, year, DateTime.UtcNow.Date);
        var baseline = await BaselineVersionAsync(c.Id, year, ct);
        var budget = await BudgetSalesByBrandAsync(c, baseline, ct);
        Dictionary<string, decimal[]> actual = new();
        try { (actual, _) = await reports.SalesActualsByBrandAsync(c, year, refresh, ct); } catch { /* seed from budget only if B1 is unreachable */ }

        var editable = (await db.Brands.AsNoTracking().Where(b => b.CompanyId == c.Id).Select(b => b.Code).ToListAsync(ct))
            .Concat(budget.Keys).Concat(actual.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && scope.Has(c.Id, code)).Distinct().ToList();

        var f = await EnsureForecastAsync(c.Id, year, ct);
        await db.SaveChangesAsync(ct);
        var existing = await db.ForecastLines.Where(l => l.ForecastId == f.Id).ToDictionaryAsync(l => l.BrandCode, ct);
        var seeded = new List<string>();
        foreach (var code in editable)
        {
            var b = budget.GetValueOrDefault(code) ?? new decimal[12];
            var a = actual.GetValueOrDefault(code) ?? new decimal[12];
            var amounts = new decimal[12];
            for (var i = 0; i < 12; i++) amounts[i] = i < elapsed ? a[i] : b[i];
            if (amounts.All(x => x == 0) && !existing.ContainsKey(code)) continue;   // nothing to seed for a brand with no budget/actual
            if (!existing.TryGetValue(code, out var line)) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = code });
            line.Amounts = amounts;
            seeded.Add(code);
        }
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return seeded;
    }
}
