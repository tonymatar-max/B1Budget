namespace B1Budget.Api.Services;

/// <summary>Fiscal year N starts on day 1 of <c>startMonth</c> in calendar year N and has 12 monthly periods.</summary>
public readonly record struct FiscalCalendar(int StartMonth)
{
    public DateTime YearStart(int fiscalYear) => new(fiscalYear, StartMonth, 1);
    public DateTime PeriodStart(int fiscalYear, int period) => YearStart(fiscalYear).AddMonths(period - 1);
    public DateTime PeriodEnd(int fiscalYear, int period) => PeriodStart(fiscalYear, period).AddMonths(1).AddDays(-1);

    /// <summary>1–12, or 0 when the date falls outside the fiscal year.</summary>
    public int PeriodOf(int fiscalYear, DateTime date)
    {
        var start = YearStart(fiscalYear);
        var months = (date.Year - start.Year) * 12 + date.Month - start.Month;
        return months is >= 0 and < 12 ? months + 1 : 0;
    }

    public string PeriodLabel(int fiscalYear, int period) => PeriodStart(fiscalYear, period).ToString("MMM yy");

    /// <summary>The fiscal year containing <paramref name="date"/>.</summary>
    public int FiscalYearOf(DateTime date) => date.Month >= StartMonth ? date.Year : date.Year - 1;
}
