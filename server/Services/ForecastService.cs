using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public record ForecastBrandRow(string Brand, string BrandName, bool CanEdit,
    decimal[] Forecast, decimal[] Budget, decimal[] Actual, ForecastFlow Flow);

public record SeedResult(List<string> Seeded, int Locked);

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
    string? ActualsError, ForecastMemberDto[] ItemGroups, string Group, ForecastMeasure Measure, ForecastFlow? Whole);

/// <summary>
/// Sales forecast for one company, fiscal year and <see cref="ForecastBasis"/>: expected sales per member per
/// month. A member is a cost center (dimension), item group, item, or item-UDF value. Actual sales come from
/// journal lines (dimension) or sales invoices (item bases). The sales budget baseline only exists for the
/// dimension basis (the app budgets by cost center × account); item bases seed the future from a run-rate.
/// App-only — nothing here is pushed to SAP B1.
/// </summary>
public class ForecastService(AppDbContext db, ReportService reports, GatewayFactory factory, ForecastWorkflowService wf)
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
    /// <summary>Quantity forecasts exist only for the Item basis; everything else is a value forecast.</summary>
    public static ForecastMeasure NormMeasure(ForecastBasis basis, ForecastMeasure measure) =>
        basis == ForecastBasis.Item ? measure : ForecastMeasure.Value;

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
        DateTime? AsOf, BudgetVersion? Baseline, string? Error)> LoadBasisAsync(Company c, ForecastBasis basis, string udf, string? group, ForecastMeasure measure, int year, bool refresh, CancellationToken ct)
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
                foreach (var m in await gw.GetForecastMembersAsync(basis, udf, group, ct)) names.TryAdd(m.Code, m.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
            try
            {
                var (byMember, t) = await reports.SalesByMemberAsync(c, basis, udf, measure, year, refresh, ct);
                asOf = t;
                foreach (var (code, (name, amounts)) in byMember) { actual[code] = amounts; if (!string.IsNullOrEmpty(name)) names[code] = name; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
        }
        return (names, budget, actual, asOf, baseline, error);
    }

    public async Task<ForecastReport> GetAsync(Company c, int? year, ForecastBasis basis, string? udf, string? group, ForecastMeasure measure, bool refresh, Scope scope, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        measure = NormMeasure(basis, measure);
        udf ??= "";
        group ??= "";
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
        // Item groups power the Item-basis group filter.
        var itemGroups = new List<ForecastMemberDto>();
        if (basis == ForecastBasis.Item)
            try { using var gw = factory.Create(c); itemGroups = await gw.GetForecastMembersAsync(ForecastBasis.ItemGroup, null, null, ct); } catch { /* best-effort */ }

        var (names, budget, actual, asOf, baseline, error) = await LoadBasisAsync(c, basis, udf, group, measure, fy, refresh, ct);

        var forecast = await db.Forecasts.AsNoTracking().Include(f => f.Lines)
            .FirstOrDefaultAsync(f => f.CompanyId == c.Id && f.FiscalYear == fy && f.Basis == basis && f.UdfName == udf && f.Measure == measure, ct);
        var forecastLines = forecast?.Lines.ToDictionary(l => l.BrandCode, l => l.Amounts) ?? new();

        // Who may see/work on which member: dimension is department-scoped; item bases are admin-only (already checked).
        bool Access(string code) => basis != ForecastBasis.Dimension ? scope.IsAdmin : scope.Has(c.Id, code);

        var codes = names.Keys.Concat(budget.Keys).Concat(forecastLines.Keys).Concat(actual.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && Access(code))
            .Distinct().OrderBy(code => code).ToList();

        // Approval state: one unit per cost center, or the whole grid ("*") on item-based bases. A submitted or approved unit is locked.
        var units = basis == ForecastBasis.Dimension ? codes : new List<string> { ForecastWorkflowService.All };
        var flows = await wf.FlowsAsync(forecast, scope, units, _ => true);   // access is already enforced by `codes`
        ForecastFlow FlowOf(string code) => flows[ForecastWorkflowService.UnitOf(basis, code)];

        var rows = codes.Select(code => new ForecastBrandRow(
            code, names.GetValueOrDefault(code, code), ForecastWorkflowService.Editable(FlowOf(code).Status),
            forecastLines.GetValueOrDefault(code) ?? new decimal[12],
            budget.GetValueOrDefault(code) ?? new decimal[12],
            actual.GetValueOrDefault(code) ?? new decimal[12],
            FlowOf(code))).ToList();

        var current2 = today > cal.PeriodEnd(fy, 12) ? 13 : cal.PeriodOf(fy, today);
        var labels = Enumerable.Range(1, 12).Select(p => cal.PeriodLabel(fy, p)).ToArray();
        return new ForecastReport(c.Id, c.Currency, fy, years, basis, udf, udfFields.ToArray(),
            basis == ForecastBasis.Dimension && baseline != null, MemberLabelFor(basis, udf),
            labels, current2, ElapsedMonths(cal, fy, today), forecast != null, forecast?.UpdatedAt, asOf,
            baseline?.Id, baseline is null ? null : $"{baseline.Name} · Rev {baseline.RevisionNo}", rows, error,
            itemGroups.OrderBy(g => g.Name).ToArray(), group, measure,
            basis == ForecastBasis.Dimension ? null : flows[ForecastWorkflowService.All]);
    }

    private async Task<SalesForecast> EnsureForecastAsync(int companyId, int year, ForecastBasis basis, string udf, ForecastMeasure measure, CancellationToken ct)
    {
        var f = await db.Forecasts.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.FiscalYear == year && x.Basis == basis && x.UdfName == udf && x.Measure == measure, ct);
        if (f is null) db.Forecasts.Add(f = new SalesForecast { CompanyId = companyId, FiscalYear = year, Basis = basis, UdfName = udf, Measure = measure });
        return f;
    }

    public async Task SaveMemberAsync(Company c, int year, ForecastBasis basis, string? udf, ForecastMeasure measure, string member, decimal[] amounts, Scope scope, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        udf ??= "";
        if (amounts.Length != 12) throw new InvalidOperationException("A forecast line needs exactly 12 monthly amounts.");
        if (basis == ForecastBasis.Dimension) scope.Require(c.Id, member);
        measure = NormMeasure(basis, measure);
        await wf.RequireEditableAsync(c.Id, year, basis, udf, measure, member);
        var f = await EnsureForecastAsync(c.Id, year, basis, udf, measure, ct);
        await db.SaveChangesAsync(ct);
        var line = await db.ForecastLines.FirstOrDefaultAsync(l => l.ForecastId == f.Id && l.BrandCode == member, ct);
        if (line is null) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = member });
        line.Amounts = amounts.ToArray();
        f.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Actual sales per member for a year (no budget/names) — used for the prior-year baseline of a projection.</summary>
    private async Task<Dictionary<string, decimal[]>> ActualsByMemberAsync(Company c, ForecastBasis basis, string udf, ForecastMeasure measure, int year, bool refresh, CancellationToken ct)
    {
        var actual = new Dictionary<string, decimal[]>();
        try
        {
            if (basis == ForecastBasis.Dimension) { var (a, _) = await reports.SalesActualsByBrandAsync(c, year, refresh, ct); actual = a; }
            else { var (byM, _) = await reports.SalesByMemberAsync(c, basis, udf, measure, year, refresh, ct); foreach (var (code, (_, amt)) in byM) actual[code] = amt; }
        }
        catch { /* prior year is best-effort */ }
        return actual;
    }

    /// <summary>Per-month average of the previous <paramref name="years"/> years' actual sales per member
    /// (year-1 … year-N). A member's months are averaged only over the prior years in which it actually sold.</summary>
    private async Task<Dictionary<string, decimal[]>> PriorYearsAverageAsync(Company c, ForecastBasis basis, string udf, ForecastMeasure measure, int year, int years, bool refresh, CancellationToken ct)
    {
        var perYear = new List<Dictionary<string, decimal[]>>();
        for (var k = 1; k <= years; k++) perYear.Add(await ActualsByMemberAsync(c, basis, udf, measure, year - k, refresh, ct));
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
    public async Task<SeedResult> SeedAsync(Company c, int year, ForecastBasis basis, string? udf, string? group, ForecastMeasure measure, ForecastMethod method, decimal growthPct, int years, Scope scope, bool refresh, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        measure = NormMeasure(basis, measure);
        udf ??= "";
        var cal = new FiscalCalendar(c.FiscalYearStartMonth);
        var elapsed = ElapsedMonths(cal, year, DateTime.UtcNow.Date);
        var (names, budget, actual, _, _, _) = await LoadBasisAsync(c, basis, udf, group, measure, year, refresh, ct);
        // Budget projection only makes sense on the dimension basis; elsewhere fall back to run-rate.
        if (method == ForecastMethod.Budget && basis != ForecastBasis.Dimension) method = ForecastMethod.RunRate;

        // Prior-year baseline: the average of the previous X years, or just last year, per the method.
        var prior = new Dictionary<string, decimal[]>();
        var projMethod = method;
        if (method == ForecastMethod.PriorYearsAverage)
        {
            prior = await PriorYearsAverageAsync(c, basis, udf, measure, year, Math.Clamp(years, 1, 10), refresh, ct);
            projMethod = ForecastMethod.PriorYearGrowth;   // project the averaged array exactly like prior-year growth
        }
        else if (method is ForecastMethod.PriorYearGrowth or ForecastMethod.SeasonalRunRate)
            prior = await ActualsByMemberAsync(c, basis, udf, measure, year - 1, refresh, ct);

        bool Access(string code) => basis != ForecastBasis.Dimension ? scope.IsAdmin : scope.Has(c.Id, code);
        var members = names.Keys.Concat(budget.Keys).Concat(actual.Keys).Concat(prior.Keys)
            .Where(code => !string.IsNullOrEmpty(code) && Access(code)).Distinct().ToList();

        // Locked (submitted/approved) units are never overwritten: skip locked cost centers, refuse a locked whole forecast.
        var current = await wf.FindAsync(c.Id, year, basis, udf, measure);
        var statuses = await wf.StatusesAsync(current?.Id);
        bool IsLocked(string code) => !ForecastWorkflowService.Editable(ForecastWorkflowService.StatusOf(statuses, ForecastWorkflowService.UnitOf(basis, code)));
        if (basis != ForecastBasis.Dimension && IsLocked(""))
            throw new InvalidOperationException("This forecast is submitted or approved and locked - reopen it before seeding.");
        var lockedCount = members.Count(IsLocked);
        members = members.Where(code => !IsLocked(code)).ToList();

        var f = await EnsureForecastAsync(c.Id, year, basis, udf, measure, ct);
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
        return new SeedResult(seeded, lockedCount);
    }

    // ------------------------------------------------------------------ Excel round-trip

    private const int FirstPeriodCol = 3;   // A = code, B = name, C..N = 12 periods, O = total

    /// <summary>
    /// Workbook of the current forecast (one row per member, 12 monthly columns) plus an "Actuals" reference sheet and an
    /// "Info" sheet that records the year/basis/measure so an import can refuse a mismatched file.
    /// </summary>
    public async Task<(byte[] Bytes, string FileName)> ExportAsync(Company c, int year, ForecastBasis basis, string? udf, string? group,
        ForecastMeasure measure, Scope scope, CancellationToken ct)
    {
        var report = await GetAsync(c, year, basis, udf, group, measure, false, scope, ct);
        var unit = report.Measure == ForecastMeasure.Quantity ? "Quantity" : "Value";

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Forecast");
        ws.Cell(1, 1).Value = report.MemberLabel; ws.Cell(1, 2).Value = "Name";
        for (var p = 0; p < 12; p++) ws.Cell(1, FirstPeriodCol + p).Value = report.PeriodLabels[p];
        ws.Cell(1, FirstPeriodCol + 12).Value = "Total";
        var row = 2;
        foreach (var r in report.Rows)
        {
            ws.Cell(row, 1).Value = r.Brand; ws.Cell(row, 2).Value = r.BrandName;
            for (var p = 0; p < 12; p++) ws.Cell(row, FirstPeriodCol + p).Value = r.Forecast[p];
            ws.Cell(row, FirstPeriodCol + 12).FormulaA1 = $"SUM({ws.Cell(row, FirstPeriodCol).Address}:{ws.Cell(row, FirstPeriodCol + 11).Address})";
            row++;
        }
        var header = ws.Range(1, 1, 1, FirstPeriodCol + 12);
        header.Style.Font.Bold = true; header.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF1FD");
        ws.Range(2, FirstPeriodCol, Math.Max(2, row - 1), FirstPeriodCol + 12).Style.NumberFormat.Format = report.Measure == ForecastMeasure.Quantity ? "#,##0.##" : "#,##0.00";
        ws.Column(2).Width = 32; ws.Columns(FirstPeriodCol, FirstPeriodCol + 12).Width = 12;
        ws.SheetView.FreezeRows(1); ws.SheetView.FreezeColumns(2);

        var act = wb.Worksheets.Add("Actuals");
        act.Cell(1, 1).Value = report.MemberLabel; act.Cell(1, 2).Value = "Name";
        for (var p = 0; p < 12; p++) act.Cell(1, FirstPeriodCol + p).Value = report.PeriodLabels[p];
        var ar = 2;
        foreach (var r in report.Rows)
        {
            act.Cell(ar, 1).Value = r.Brand; act.Cell(ar, 2).Value = r.BrandName;
            for (var p = 0; p < 12; p++) act.Cell(ar, FirstPeriodCol + p).Value = r.Actual[p];
            ar++;
        }
        act.Row(1).Style.Font.Bold = true; act.Column(2).Width = 32;

        var info = wb.Worksheets.Add("Info");
        info.Cell(1, 1).Value = "Basis"; info.Cell(1, 2).Value = basis.ToString();
        info.Cell(2, 1).Value = "Measure"; info.Cell(2, 2).Value = report.Measure.ToString();
        info.Cell(3, 1).Value = "Fiscal year"; info.Cell(3, 2).Value = year;
        info.Cell(4, 1).Value = "UDF"; info.Cell(4, 2).Value = report.Udf;
        info.Cell(6, 1).Value = $"Edit the {unit.ToLowerInvariant()} figures on the Forecast sheet (columns C-N) and import the file back. Do not change the first column (the member code).";
        info.Column(1).Width = 14; info.Column(2).Width = 22;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var tag = basis == ForecastBasis.Dimension ? "CostCenter" : basis.ToString();
        return (ms.ToArray(), $"Forecast_{year}_{tag}{(report.Measure == ForecastMeasure.Quantity ? "_Qty" : "")}.xlsx");
    }

    public record ImportResult(int Imported, int Skipped, List<string> Errors);

    /// <summary>
    /// Import a workbook produced by <see cref="ExportAsync"/> (or the same layout): the member code in column A and the 12
    /// monthly figures in C-N. Rows with an unknown or not-editable member, or a non-numeric/negative figure, are reported and skipped.
    /// </summary>
    public async Task<ImportResult> ImportAsync(Company c, int year, ForecastBasis basis, string? udf, ForecastMeasure measure,
        Stream stream, Scope scope, CancellationToken ct)
    {
        RequireBasisAccess(basis, scope);
        measure = NormMeasure(basis, measure);
        udf ??= "";

        XLWorkbook wb;
        try { wb = new XLWorkbook(stream); }
        catch (Exception) { throw new InvalidOperationException("That file isn't a readable .xlsx workbook."); }
        using (wb)
        {
            // The Info sheet (written by export) guards against importing a file into the wrong forecast.
            if (wb.Worksheets.TryGetWorksheet("Info", out var info))
            {
                string Cell(int r) => info.Cell(r, 2).GetString().Trim();
                if (Cell(1) != "" && !string.Equals(Cell(1), basis.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"This file is a {Cell(1)} forecast but you are importing into {basis}.");
                if (Cell(2) != "" && !string.Equals(Cell(2), measure.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"This file holds {Cell(2).ToLowerInvariant()} figures but you are importing into the {measure.ToString().ToLowerInvariant()} forecast.");
                if (int.TryParse(Cell(3), out var fileYear) && fileYear != year)
                    throw new InvalidOperationException($"This file is for FY {fileYear} but you are importing into FY {year}.");
                if (basis == ForecastBasis.ItemUdf && Cell(4) != "" && !string.Equals(Cell(4), udf, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"This file is grouped by {Cell(4)} but you are importing into {udf}.");
            }

            var ws = wb.Worksheets.TryGetWorksheet("Forecast", out var fs) ? fs : wb.Worksheet(1);
            // Valid members = everything the page would list for this forecast (no group filter, so a filtered export still imports).
            var current = await GetAsync(c, year, basis, udf, null, measure, false, scope, ct);
            if (current.Whole is { } whole && !ForecastWorkflowService.Editable(whole.Status))
                throw new InvalidOperationException($"This forecast is {(whole.Status == DeptStatus.Submitted ? "waiting for approval" : "approved")} and locked - reopen it before importing.");
            var canon = current.Rows.ToDictionary(r => r.Brand, r => r, StringComparer.OrdinalIgnoreCase);

            var errors = new List<string>();
            var parsed = new Dictionary<string, decimal[]>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            foreach (var xr in ws.RowsUsed().Skip(1))
            {
                var n = xr.RowNumber();
                var code = xr.Cell(1).GetString().Trim();
                if (code == "") continue;
                if (!canon.TryGetValue(code, out var target)) { errors.Add($"Row {n}: unknown {MemberLabelFor(basis, udf).ToLowerInvariant()} '{code}'."); skipped++; continue; }
                var member = target.Brand;
                if (!target.CanEdit) { errors.Add($"Row {n} ({member}): locked - {(target.Flow.Status == DeptStatus.Submitted ? "waiting for approval" : "approved")}; reopen it to change it."); skipped++; continue; }
                var amounts = new decimal[12];
                var bad = false;
                for (var p = 0; p < 12 && !bad; p++)
                {
                    var cell = xr.Cell(FirstPeriodCol + p);
                    if (cell.IsEmpty()) continue;
                    decimal v;
                    if (cell.DataType == XLDataType.Number) v = (decimal)cell.GetDouble();
                    else if (!decimal.TryParse(cell.GetString().Replace(",", "").Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out v))
                    { errors.Add($"Row {n} ({member}): '{cell.GetString()}' in column {cell.Address.ColumnLetter} is not a number."); bad = true; continue; }
                    if (v < 0) { errors.Add($"Row {n} ({member}): negative figure in column {cell.Address.ColumnLetter}."); bad = true; continue; }
                    amounts[p] = Math.Round(v, 4);
                }
                if (bad) { skipped++; continue; }
                parsed[member] = amounts;   // a repeated code: the last row wins
            }

            if (parsed.Count > 0)
            {
                var f = await EnsureForecastAsync(c.Id, year, basis, udf, measure, ct);
                await db.SaveChangesAsync(ct);
                var existing = await db.ForecastLines.Where(l => l.ForecastId == f.Id).ToDictionaryAsync(l => l.BrandCode, StringComparer.OrdinalIgnoreCase, ct);
                foreach (var (code, amounts) in parsed)
                {
                    if (!existing.TryGetValue(code, out var line)) db.ForecastLines.Add(line = new SalesForecastLine { ForecastId = f.Id, BrandCode = code });
                    line.Amounts = amounts;
                }
                f.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return new ImportResult(parsed.Count, skipped, errors);
        }
    }
}
