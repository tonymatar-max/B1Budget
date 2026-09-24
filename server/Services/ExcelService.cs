using B1Budget.Api.Domain;
using ClosedXML.Excel;

namespace B1Budget.Api.Services;

/// <summary>Excel round-trip for a budget version: Brand | Brand name | Account | Account name | Type | 12 periods | Total.</summary>
public static class ExcelService
{
    private const int FirstPeriodCol = 6;

    public static byte[] Export(BudgetVersion version, IEnumerable<Brand> brands, IEnumerable<GlAccount> accounts, FiscalCalendar cal)
    {
        var brandNames = brands.ToDictionary(b => b.Code, b => b.Name);
        var accs = accounts.ToDictionary(a => a.Code);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Budget");
        string[] head = ["Brand", "Brand name", "Account", "Account name", "Type"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        for (var p = 1; p <= 12; p++) ws.Cell(1, FirstPeriodCol + p - 1).Value = cal.PeriodLabel(version.FiscalYear, p);
        ws.Cell(1, FirstPeriodCol + 12).Value = "Total";

        var row = 2;
        foreach (var l in version.Lines.OrderBy(l => l.BrandCode).ThenBy(l => l.AccountCode))
        {
            accs.TryGetValue(l.AccountCode, out var acc);
            ws.Cell(row, 1).Value = l.BrandCode;
            ws.Cell(row, 2).Value = brandNames.GetValueOrDefault(l.BrandCode, "");
            ws.Cell(row, 3).Value = l.AccountCode;
            ws.Cell(row, 4).Value = acc?.Name ?? "";
            ws.Cell(row, 5).Value = acc?.Kind.ToString() ?? "";
            for (var p = 0; p < 12; p++) ws.Cell(row, FirstPeriodCol + p).Value = l.Amounts[p];
            ws.Cell(row, FirstPeriodCol + 12).FormulaA1 =
                $"SUM({ws.Cell(row, FirstPeriodCol).Address}:{ws.Cell(row, FirstPeriodCol + 11).Address})";
            row++;
        }

        var header = ws.Range(1, 1, 1, FirstPeriodCol + 12);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF1FD");
        ws.Range(2, FirstPeriodCol, Math.Max(2, row), FirstPeriodCol + 12).Style.NumberFormat.Format = "#,##0.00";
        ws.Columns(2, 4).Width = 26;
        ws.Columns(FirstPeriodCol, FirstPeriodCol + 12).Width = 12;
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(4);

        // Reference sheet so users can pick valid codes.
        var refs = wb.Worksheets.Add("Codes");
        refs.Cell(1, 1).Value = "Brand"; refs.Cell(1, 2).Value = "Name";
        refs.Cell(1, 4).Value = "Account"; refs.Cell(1, 5).Value = "Name"; refs.Cell(1, 6).Value = "Type";
        var r = 2;
        foreach (var (code, name) in brandNames.OrderBy(b => b.Key)) { refs.Cell(r, 1).Value = code; refs.Cell(r, 2).Value = name; r++; }
        r = 2;
        foreach (var a in accs.Values.OrderBy(a => a.Code))
        { refs.Cell(r, 4).Value = a.Code; refs.Cell(r, 5).Value = a.Name; refs.Cell(r, 6).Value = a.Kind.ToString(); r++; }
        refs.Row(1).Style.Font.Bold = true;
        refs.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public record ImportResult(List<BudgetLine> Lines, List<string> Errors);

    public static ImportResult Import(Stream stream, ISet<string> brandCodes, ISet<string> accountCodes)
    {
        var lines = new Dictionary<(string, string), BudgetLine>();
        var errors = new List<string>();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.Contains("Budget") ? wb.Worksheet("Budget") : wb.Worksheet(1);

        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var n = row.RowNumber();
            var brand = row.Cell(1).GetString().Trim();
            var account = row.Cell(3).GetString().Trim();
            if (brand == "" && account == "") continue;
            if (!brandCodes.Contains(brand)) { errors.Add($"Row {n}: unknown brand '{brand}'."); continue; }
            if (!accountCodes.Contains(account)) { errors.Add($"Row {n}: unknown or non-P&L account '{account}'."); continue; }

            var amounts = new decimal[12];
            var bad = false;
            for (var p = 0; p < 12; p++)
            {
                var c = row.Cell(FirstPeriodCol + p);
                if (c.IsEmpty()) continue;
                if (c.TryGetValue<decimal>(out var d)) amounts[p] = Math.Round(d, 2);
                else { errors.Add($"Row {n}: period {p + 1} value '{c.GetString()}' is not a number."); bad = true; }
            }
            if (bad) continue;

            if (lines.TryGetValue((brand, account), out var existing))
            {
                for (var p = 0; p < 12; p++) existing.Amounts[p] += amounts[p];
                errors.Add($"Row {n}: {brand}/{account} appears more than once — amounts were added together.");
            }
            else lines[(brand, account)] = new BudgetLine { BrandCode = brand, AccountCode = account, Amounts = amounts };
        }
        return new ImportResult(lines.Values.ToList(), errors);
    }
}
