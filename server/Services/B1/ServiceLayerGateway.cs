using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using B1Budget.Api.Domain;

namespace B1Budget.Api.Services.B1;

/// <summary>Live SAP B1 over Service Layer. Works on both HANA and SQL Server companies.</summary>
public class ServiceLayerGateway(ServiceLayerClient sl, string? fieldMapOverride) : IB1Gateway
{
    private BudgetFieldMap? _map;

    // JDT1 column holding each cost-accounting dimension's distribution rule.
    public static string DimColumn(int dim) => dim switch
    {
        1 => "ProfitCode",
        >= 2 and <= 5 => "OcrCode" + dim,
        _ => throw new ArgumentOutOfRangeException(nameof(dim), "Dimension must be 1–5."),
    };

    public async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        await sl.LoginAsync(ct);
        try
        {
            var co = await sl.PostAsync("CompanyService_GetCompanyInfo", new JsonObject(), ct);
            var name = co["CompanyName"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name)) return $"Connected to {name}.";
        }
        catch (ServiceLayerException) { /* info is optional; login succeeding is what matters */ }
        return "Connected.";
    }

    public async Task<List<BrandDto>> GetBrandsAsync(int dimension, CancellationToken ct)
    {
        var rows = await sl.QueryAllAsync(
            $"DistributionRules?$select=FactorCode,FactorDescription,InWhichDimension,Active&$filter=InWhichDimension eq {dimension}", ct);
        return rows.Select(r => new BrandDto(
            Str(r, "FactorCode"), Str(r, "FactorDescription"), Str(r, "Active") != "tNO")).ToList();
    }

    // Postable income-statement accounts. OACT.GroupMask is the reliable P&L test (4 revenue, 5 cost of sales,
    // 6 operating costs, 7 financing, 8 other) — AccountType alone is not: demo DBs flag equity accounts as at_Expenses.
    private const string AccountsSql =
        "SELECT T0.AcctCode, T0.AcctName, T0.FormatCode, T0.ActType, T0.GroupMask FROM OACT T0 " +
        "WHERE T0.Postable = 'Y' AND T0.GroupMask >= 4 AND T0.GroupMask <= 8";

    public async Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct)
    {
        try
        {
            await sl.EnsureSqlQueryAsync("NXBGT_COA", "Cobalt Budget P&L accounts", AccountsSql, ct);
            var rows = await sl.RunSqlQueryAsync("NXBGT_COA", new Dictionary<string, string>(), ct);
            return rows.Select(r => new AccountDto(
                Str(r, "AcctCode"), Str(r, "AcctName"),
                Str(r, "ActType") == "I" || Dec(r, "GroupMask") == 4 ? AccountKind.Revenue : AccountKind.Expense,
                NullIfEmpty(Str(r, "FormatCode")))).ToList();
        }
        catch (ServiceLayerException)
        {
            // OACT not allowed for SQLQueries — fall back to OData, restricted to budget-relevant accounts.
            var rows = await sl.QueryAllAsync(
                "ChartOfAccounts?$select=Code,Name,AccountType,FormatCode&$filter=ActiveAccount eq 'tYES' and BudgetAccount eq 'tYES' and AccountType ne 'at_Other'", ct);
            return rows.Select(r => new AccountDto(
                Str(r, "Code"), Str(r, "Name"),
                Str(r, "AccountType") == "at_Revenues" ? AccountKind.Revenue : AccountKind.Expense,
                NullIfEmpty(Str(r, "FormatCode")))).ToList();
        }
    }

    // Period-end closing entries (TransType -3) zero out P&L accounts — exclude them from actuals.
    private static string ActualsSql(int dim) =>
        $"SELECT T0.Account, T0.{DimColumn(dim)} AS Brand, T0.RefDate, SUM(T0.Debit) AS Debit, SUM(T0.Credit) AS Credit " +
        "FROM JDT1 T0 WHERE T0.RefDate >= :fromDate AND T0.RefDate <= :toDate AND T0.TransType <> '-3' " +
        $"GROUP BY T0.Account, T0.{DimColumn(dim)}, T0.RefDate";

    private static string LinesSql(int dim) =>
        $"SELECT T0.TransId, T0.Line_ID, T0.RefDate, T0.Account, T0.{DimColumn(dim)} AS Brand, T0.Debit, T0.Credit, " +
        "T0.LineMemo, T0.Ref1, T0.TransType FROM JDT1 T0 " +
        $"WHERE T0.Account = :acct AND T0.{DimColumn(dim)} = :brand AND T0.RefDate >= :fromDate AND T0.RefDate <= :toDate " +
        "AND T0.TransType <> '-3' ORDER BY T0.RefDate, T0.TransId";

    private static string LinesNoBrandSql(int dim) =>
        $"SELECT T0.TransId, T0.Line_ID, T0.RefDate, T0.Account, T0.{DimColumn(dim)} AS Brand, T0.Debit, T0.Credit, " +
        "T0.LineMemo, T0.Ref1, T0.TransType FROM JDT1 T0 " +
        $"WHERE T0.Account = :acct AND T0.{DimColumn(dim)} IS NULL AND T0.RefDate >= :fromDate AND T0.RefDate <= :toDate " +
        "AND T0.TransType <> '-3' ORDER BY T0.RefDate, T0.TransId";

    public async Task<List<ActualRow>> GetActualsAsync(int dimension, DateTime from, DateTime to, CancellationToken ct)
    {
        var code = $"NXBGT_ACT{dimension}";
        await sl.EnsureSqlQueryAsync(code, $"Cobalt Budget actuals (dim {dimension})", ActualsSql(dimension), ct);
        var rows = await sl.RunSqlQueryAsync(code, new Dictionary<string, string>
        {
            ["fromDate"] = $"'{from:yyyy-MM-dd}'", ["toDate"] = $"'{to:yyyy-MM-dd}'",
        }, ct);
        return rows.Select(r => new ActualRow(Str(r, "Account"), Str(r, "Brand"), Date(r, "RefDate"), Dec(r, "Debit"), Dec(r, "Credit"))).ToList();
    }

    /// <summary>Raw view of what Service Layer returns for the actuals query and the chart of accounts — for troubleshooting.</summary>
    public async Task<JsonObject> DiagnoseAsync(int dimension, DateTime from, DateTime to, CancellationToken ct)
    {
        var code = $"NXBGT_ACT{dimension}";
        await sl.EnsureSqlQueryAsync(code, $"Cobalt Budget actuals (dim {dimension})", ActualsSql(dimension), ct);
        var stored = await sl.GetAsync($"SQLQueries('{code}')", ct);
        var rows = await sl.RunSqlQueryAsync(code, new Dictionary<string, string>
        {
            ["fromDate"] = $"'{from:yyyy-MM-dd}'", ["toDate"] = $"'{to:yyyy-MM-dd}'",
        }, ct);
        var parsed = new JsonArray();
        foreach (var r in rows.Take(5))
            parsed.Add(new JsonObject
            {
                ["Account"] = Str(r, "Account"), ["Brand"] = Str(r, "Brand"),
                ["Date"] = Date(r, "RefDate").ToString("yyyy-MM-dd"), ["Debit"] = Dec(r, "Debit"), ["Credit"] = Dec(r, "Credit"),
            });
        return new JsonObject
        {
            ["storedSql"] = stored["SqlText"]?.DeepClone(),
            ["rowCount"] = rows.Count,
            ["rawSample"] = new JsonArray(rows.Take(5).Select(r => (JsonNode)r.DeepClone()).ToArray()),
            ["parsedSample"] = parsed,
            ["plAccounts"] = new JsonArray((await GetAccountsAsync(ct)).Select(a => (JsonNode)$"{a.Code} {a.Kind} {a.Name}").ToArray()),
        };
    }

    public async Task<List<JournalLineDto>> GetJournalLinesAsync(int dimension, string account, string brand, DateTime from, DateTime to, CancellationToken ct)
    {
        var noBrand = string.IsNullOrEmpty(brand);
        var code = noBrand ? $"NXBGT_JEN{dimension}" : $"NXBGT_JE{dimension}";
        await sl.EnsureSqlQueryAsync(code, $"Cobalt Budget drill-down (dim {dimension})", noBrand ? LinesNoBrandSql(dimension) : LinesSql(dimension), ct);
        var p = new Dictionary<string, string>
        {
            ["acct"] = Quote(account), ["fromDate"] = $"'{from:yyyy-MM-dd}'", ["toDate"] = $"'{to:yyyy-MM-dd}'",
        };
        if (!noBrand) p["brand"] = Quote(brand);
        var rows = await sl.RunSqlQueryAsync(code, p, ct);
        return rows.Select(r => new JournalLineDto(
            (int)Dec(r, "TransId"), (int)Dec(r, "Line_ID"), Date(r, "RefDate"), Str(r, "Account"), Str(r, "Brand"),
            Dec(r, "Debit"), Dec(r, "Credit"), NullIfEmpty(Str(r, "LineMemo")), NullIfEmpty(Str(r, "Ref1")), NullIfEmpty(Str(r, "TransType")))).ToList();
    }

    // ---------------------------------------------------------------- item-based forecasting (sales documents)
    // NOTE: not yet verified against a live company. OINV/INV1/ORIN/RIN1/OITM/OITB must be allowed in the
    // Service Layer SQL table whitelist (b1s_sqltable.conf), like JDT1 for actuals.

    private static readonly Regex UdfName = new("^U_[A-Za-z0-9_]{1,60}$", RegexOptions.Compiled);
    private static string RequireUdf(string? udf) =>
        !string.IsNullOrWhiteSpace(udf) && UdfName.IsMatch(udf) ? udf
        : throw new InvalidOperationException("Choose a valid item UDF (a U_… field) to forecast by.");

    public async Task<List<string>> GetItemUdfFieldsAsync(CancellationToken ct)
    {
        try
        {
            var doc = XDocument.Parse(await sl.GetMetadataAsync(ct));
            var item = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "EntityType" && (string?)e.Attribute("Name") == "Item");
            if (item is null) return new();
            return item.Elements().Where(e => e.Name.LocalName == "Property")
                .Select(e => (string?)e.Attribute("Name") ?? "")
                .Where(n => n.StartsWith("U_", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(); }
    }

    public async Task<List<ForecastMemberDto>> GetForecastMembersAsync(ForecastBasis basis, string? udf, CancellationToken ct)
    {
        switch (basis)
        {
            case ForecastBasis.ItemGroup:
                var groups = await sl.QueryAllAsync("ItemGroups?$select=Number,GroupName", ct);
                return groups.Select(g => new ForecastMemberDto(Str(g, "Number"), Str(g, "GroupName"))).ToList();
            case ForecastBasis.ItemUdf:
                var u = RequireUdf(udf);
                await sl.EnsureSqlQueryAsync("NXBGT_UDFVAL", "Cobalt Budget UDF values",
                    $"SELECT DISTINCT T0.{u} AS Val FROM OITM T0 WHERE T0.{u} IS NOT NULL AND T0.{u} <> ''", ct);
                var vals = await sl.RunSqlQueryAsync("NXBGT_UDFVAL", new Dictionary<string, string>(), ct);
                return vals.Select(v => Str(v, "Val")).Where(s => s.Length > 0).Distinct()
                    .Select(s => new ForecastMemberDto(s, s)).ToList();
            default:
                // Item basis members are derived from sales (the item master can be huge); Dimension uses synced brands.
                return new();
        }
    }

    public async Task<(List<MemberSalesRow> Rows, DateTime AsOf)> GetSalesByMemberAsync(ForecastBasis basis, string? udf, DateTime from, DateTime to, CancellationToken ct)
    {
        // member/name column expressions and the join for each basis
        var (member, name, join, code) = basis switch
        {
            ForecastBasis.ItemGroup => ("T2.ItmsGrpCod", "T3.ItmsGrpNam", " INNER JOIN OITB T3 ON T3.ItmsGrpCod = T2.ItmsGrpCod", "G"),
            ForecastBasis.Item => ("T1.ItemCode", "T1.Dscription", "", "I"),
            ForecastBasis.ItemUdf => ($"T2.{RequireUdf(udf)}", $"T2.{RequireUdf(udf)}", "", "U"),
            _ => throw new InvalidOperationException("Item-based sales are only for item / item-group / item-UDF forecasts."),
        };

        // Net sales = invoices − credit memos, by member and posting date. One stored query per basis.
        string Branch(string lines, string header, decimal sign) =>
            $"SELECT {member} AS Member, {name} AS MemberName, T0.DocDate AS D, SUM({(sign < 0 ? "-" : "")}T1.LineTotal) AS Amount " +
            $"FROM {lines} T1 INNER JOIN {header} T0 ON T0.DocEntry = T1.DocEntry " +
            $"INNER JOIN OITM T2 ON T2.ItemCode = T1.ItemCode{join} " +
            "WHERE T0.DocDate >= :fromDate AND T0.DocDate <= :toDate " +
            $"GROUP BY {member}, {name}, T0.DocDate";
        var sql = Branch("INV1", "OINV", 1) + " UNION ALL " + Branch("RIN1", "ORIN", -1);

        var qcode = $"NXBGT_SALE_{code}";
        await sl.EnsureSqlQueryAsync(qcode, $"Cobalt Budget sales by member ({code})", sql, ct);
        var rows = await sl.RunSqlQueryAsync(qcode, new Dictionary<string, string>
        {
            ["fromDate"] = $"'{from:yyyy-MM-dd}'", ["toDate"] = $"'{to:yyyy-MM-dd}'",
        }, ct);
        var result = rows.Select(r => new MemberSalesRow(Str(r, "Member"), NullIfEmpty(Str(r, "MemberName")) ?? Str(r, "Member"), Date(r, "D"), Dec(r, "Amount")))
            .Where(r => r.Member.Length > 0).ToList();
        return (result, DateTime.UtcNow);
    }

    // ---------------------------------------------------------------- native budgets

    public async Task<BudgetFieldMap> GetBudgetFieldMapAsync(CancellationToken ct)
    {
        if (_map != null) return _map;
        try { _map = BudgetFieldMap.Resolve(await sl.GetMetadataAsync(ct), fieldMapOverride); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _map = BudgetFieldMap.ApplyOverride(new BudgetFieldMap(), fieldMapOverride);
            _map.Notes.Add("Could not read $metadata (" + ex.Message + ") — using default field names.");
        }
        return _map;
    }

    public async Task<int?> FindScenarioAsync(string name, DateTime fiscalYearStart, CancellationToken ct)
    {
        var m = await GetBudgetFieldMapAsync(ct);
        var existing = await sl.QueryAllAsync($"{m.ScenarioSet}?$filter={m.ScenarioName} eq {Quote(name)}", ct);
        // Same name can exist once per fiscal year (B1 creates a "Main Budget" for every year) — match both.
        var match = existing.FirstOrDefault(s => Date(s, m.ScenarioStart).Date == fiscalYearStart.Date);
        if (match is null) return null;
        return (int)Dec(match, m.ScenarioKey);
    }

    public async Task<(int Numerator, bool Created)> EnsureScenarioAsync(string name, DateTime fiscalYearStart, string? costCenter, CancellationToken ct)
    {
        var m = await GetBudgetFieldMapAsync(ct);
        var setDim = m.ScenarioDimension != null && !string.IsNullOrWhiteSpace(costCenter);

        if (await FindScenarioAsync(name, fiscalYearStart, ct) is int found)
        {
            // Existing scenario: stamp the cost center only if B1 has none yet, so a value set by hand is never overwritten.
            if (setDim)
            {
                var rows = await sl.QueryAllAsync(
                    $"{m.ScenarioSet}?$select={m.ScenarioKey},{m.ScenarioDimension}&$filter={m.ScenarioName} eq {Quote(name)}", ct);
                var row = rows.FirstOrDefault(r => (int)Dec(r, m.ScenarioKey) == found);
                if (row is not null && string.IsNullOrWhiteSpace(Str(row, m.ScenarioDimension!)))
                    await sl.PatchAsync($"{m.ScenarioSet}({found})", new JsonObject { [m.ScenarioDimension!] = costCenter }, ct);
            }
            return (found, false);
        }

        var body = new JsonObject { [m.ScenarioName] = name, [m.ScenarioStart] = fiscalYearStart.ToString("yyyy-MM-dd") };
        if (m.ScenarioRatio != null) body[m.ScenarioRatio] = 100;
        if (setDim) body[m.ScenarioDimension!] = costCenter;
        var created = await sl.PostAsync(m.ScenarioSet, body, ct);
        return ((int)Dec(created, m.ScenarioKey), true);
    }

    public async Task<UpsertResult> UpsertBudgetAsync(int scenario, string account, AccountKind kind, decimal[] periods, CancellationToken ct)
    {
        var m = await GetBudgetFieldMapAsync(ct);
        // Revenue budgets sit on the credit side, expense budgets on the debit side.
        var amountField = kind == AccountKind.Revenue ? m.LineCredit : m.LineDebit;
        var otherField = kind == AccountKind.Revenue ? m.LineDebit : m.LineCredit;
        var headerField = kind == AccountKind.Revenue ? m.HeaderCredit : m.HeaderDebit;
        var headerOther = kind == AccountKind.Revenue ? m.HeaderDebit : m.HeaderCredit;
        var annual = periods.Sum();

        var found = await sl.QueryAllAsync(
            $"{m.BudgetSet}?$filter={m.BudgetAccount} eq {Quote(account)} and {m.BudgetScenario} eq {scenario}", ct);

        if (found.Count == 0)
        {
            var lines = new JsonArray();
            foreach (var amt in periods)
                lines.Add(new JsonObject { [amountField] = amt, [otherField] = 0m });
            var body = new JsonObject
            {
                [m.BudgetAccount] = account,
                [m.BudgetScenario] = scenario,
                [m.Lines] = lines,
            };
            if (headerField != null) body[headerField] = annual;
            if (headerOther != null) body[headerOther] = 0m;
            var created = await sl.PostAsync(m.BudgetSet, body, ct);
            return new UpsertResult(PushAction.Created, created[m.BudgetKey]?.ToString());
        }

        // Update in place: keep B1's own line objects (and any line keys) and change only the amounts.
        var current = found[0];
        var key = current[m.BudgetKey]?.ToString() ?? throw new InvalidOperationException("Existing budget has no key.");
        if (current[m.Lines] is not JsonArray curLines || curLines.Count == 0)
            throw new InvalidOperationException($"Existing budget {key} returned no '{m.Lines}'.");
        if (curLines.Count < 12)
            throw new InvalidOperationException($"Budget {key} has {curLines.Count} period lines; this app budgets 12 monthly periods.");

        var changed = false;
        var newLines = new JsonArray();
        for (var i = 0; i < curLines.Count; i++)
        {
            var line = (JsonObject)curLines[i]!.DeepClone();
            var target = i < 12 ? periods[i] : 0m;
            if (Dec(line, amountField) != target || Dec(line, otherField) != 0m) changed = true;
            line[amountField] = target;
            line[otherField] = 0m;
            newLines.Add(line);
        }
        if (!changed) return new UpsertResult(PushAction.Unchanged, key);

        var patch = new JsonObject { [m.Lines] = newLines };
        if (headerField != null) patch[headerField] = annual;
        if (headerOther != null) patch[headerOther] = 0m;
        await sl.PatchAsync($"{m.BudgetSet}({key})", patch, ct);
        return new UpsertResult(PushAction.Updated, key);
    }

    public async Task<bool> ClearBudgetAsync(int scenario, string account, AccountKind kind, CancellationToken ct)
    {
        var m = await GetBudgetFieldMapAsync(ct);
        var found = await sl.QueryAllAsync(
            $"{m.BudgetSet}?$select={m.BudgetKey}&$filter={m.BudgetAccount} eq {Quote(account)} and {m.BudgetScenario} eq {scenario}", ct);
        if (found.Count == 0) return false;
        return (await UpsertBudgetAsync(scenario, account, kind, new decimal[12], ct)).Action != PushAction.Unchanged;
    }

    public async Task<Dictionary<string, decimal[]>> ReadScenarioBudgetsAsync(int scenario, CancellationToken ct)
    {
        var m = await GetBudgetFieldMapAsync(ct);
        var rows = await sl.QueryAllAsync($"{m.BudgetSet}?$filter={m.BudgetScenario} eq {scenario}", ct);
        var result = new Dictionary<string, decimal[]>();
        foreach (var r in rows)
        {
            var arr = new decimal[12];
            if (r[m.Lines] is JsonArray lines)
                for (var i = 0; i < Math.Min(12, lines.Count); i++)
                    if (lines[i] is JsonObject l) arr[i] = Dec(l, m.LineDebit) + Dec(l, m.LineCredit);
            result[Str(r, m.BudgetAccount)] = arr;
        }
        return result;
    }

    // ---------------------------------------------------------------- json helpers

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Str(JsonObject o, string key) => o[key] switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var n => n.ToString(),
    };

    private static decimal Dec(JsonObject o, string key)
    {
        var n = o[key];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return 0m;
    }

    // SQLQueries returns dates as "yyyyMMdd"; OData entities return ISO ("2026-01-01T00:00:00Z").
    private static readonly string[] DateFormats = ["yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ"];

    private static DateTime Date(JsonObject o, string key)
    {
        var s = Str(o, key).Trim();
        if (s.Length == 0) return default;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return d.Date;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d)
            ? d.Date : throw new FormatException($"Unrecognised date '{s}' in field {key}.");
    }

    public void Dispose() => sl.Dispose();
}
