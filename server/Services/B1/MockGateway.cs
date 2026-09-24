using B1Budget.Api.Domain;

namespace B1Budget.Api.Services.B1;

/// <summary>
/// In-memory stand-in for SAP B1 so the app can be demoed and developed without a company DB.
/// Actuals are deterministic (seeded per account/brand/day); pushed budgets are kept for the process lifetime.
/// </summary>
public class MockGateway(int companyId) : IB1Gateway
{
    /// <summary>One simulated B1 database per company, kept for the process lifetime.</summary>
    private sealed class Store
    {
        public readonly Dictionary<int, (string Name, DateTime Start)> Scenarios = new() { [1] = ("Main Budget", new DateTime(DateTime.UtcNow.Year, 1, 1)) };
        public readonly Dictionary<(int Scenario, string Account), (int Key, decimal[] Periods)> Budgets = new();
        public int NextBudgetKey = 1;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Store> Stores = new();
    private Store Db { get { lock (Gate) return Stores.TryGetValue(companyId, out var s) ? s : Stores[companyId] = new Store(); } }
    private Dictionary<int, (string Name, DateTime Start)> Scenarios => Db.Scenarios;
    private Dictionary<(int Scenario, string Account), (int Key, decimal[] Periods)> Budgets => Db.Budgets;

    private static readonly BrandDto[] BrandList =
    [
        new("NIKE", "Nike", true), new("ADID", "Adidas", true), new("PUMA", "Puma", true),
        new("NB", "New Balance", true), new("SKCH", "Skechers", true), new("OLD", "Discontinued brand", false),
    ];

    private static readonly AccountDto[] AccountList =
    [
        new("410000", "Sales – Retail", AccountKind.Revenue, "4-10-000"),
        new("411000", "Sales – Wholesale", AccountKind.Revenue, "4-11-000"),
        new("412000", "Sales – E-commerce", AccountKind.Revenue, "4-12-000"),
        new("510000", "Cost of Goods Sold", AccountKind.Expense, "5-10-000"),
        new("610000", "Salaries & Wages", AccountKind.Expense, "6-10-000"),
        new("620000", "Rent – Stores", AccountKind.Expense, "6-20-000"),
        new("630000", "Marketing & Advertising", AccountKind.Expense, "6-30-000"),
        new("631000", "Brand Co-op Marketing", AccountKind.Expense, "6-31-000"),
        new("640000", "Utilities", AccountKind.Expense, "6-40-000"),
        new("650000", "Travel & Entertainment", AccountKind.Expense, "6-50-000"),
        new("660000", "Freight Out", AccountKind.Expense, "6-60-000"),
    ];

    // Monthly base (local currency) per account; brands scale it.
    private static readonly Dictionary<string, decimal> Base = new()
    {
        ["410000"] = 42000, ["411000"] = 26000, ["412000"] = 9000, ["510000"] = 41000, ["610000"] = 11000,
        ["620000"] = 6500, ["630000"] = 3800, ["631000"] = 1500, ["640000"] = 900, ["650000"] = 700, ["660000"] = 1300,
    };
    private static readonly Dictionary<string, decimal> BrandScale = new()
    {
        ["NIKE"] = 1.6m, ["ADID"] = 1.25m, ["PUMA"] = 0.7m, ["NB"] = 0.55m, ["SKCH"] = 0.45m, [""] = 0.03m,
    };

    public Task<string> TestConnectionAsync(CancellationToken ct) => Task.FromResult("Mock mode — demo data, nothing is sent to SAP B1.");

    public Task<List<BrandDto>> GetBrandsAsync(int dimension, CancellationToken ct) => Task.FromResult(BrandList.ToList());
    public Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct) => Task.FromResult(AccountList.ToList());

    public Task<List<ActualRow>> GetActualsAsync(int dimension, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = new List<ActualRow>();
        var companyScale = 1m + (companyId - 1) * 0.35m;
        var today = DateTime.UtcNow.Date;
        foreach (var (brand, scale) in BrandScale)
            foreach (var acc in AccountList)
            {
                // Unassigned-brand postings only on a couple of expense accounts (realistic "missing cost center").
                if (brand == "" && acc.Code is not ("640000" or "650000")) continue;
                for (var d = from.Date; d <= to.Date && d <= today; d = d.AddDays(1))
                {
                    if (d.DayOfWeek == DayOfWeek.Friday) continue;
                    var amt = Math.Round(DailyAmount(acc.Code, brand, d) * scale * companyScale, 2);
                    if (amt == 0) continue;
                    rows.Add(acc.Kind == AccountKind.Revenue
                        ? new ActualRow(acc.Code, brand, d, 0, amt)
                        : new ActualRow(acc.Code, brand, d, amt, 0));
                }
            }
        return Task.FromResult(rows);
    }

    public async Task<List<JournalLineDto>> GetJournalLinesAsync(int dimension, string account, string brand, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = (await GetActualsAsync(dimension, from, to, ct)).Where(r => r.Account == account && r.Brand == brand).ToList();
        var memo = AccountList.FirstOrDefault(a => a.Code == account)?.Name ?? account;
        return rows.Select((r, i) => new JournalLineDto(
            100000 + r.Date.DayOfYear * 10 + i % 10, 0, r.Date, r.Account, r.Brand, r.Debit, r.Credit,
            $"{memo} – {r.Date:dd MMM}", $"INV{r.Date:yyMMdd}", r.Credit > 0 ? "13" : "18")).ToList();
    }

    private static decimal DailyAmount(string account, string brand, DateTime d)
    {
        var baseMonthly = Base.GetValueOrDefault(account);
        var seasonal = 1 + 0.25 * Math.Sin((d.Month - 3) / 12.0 * 2 * Math.PI) + (d.Month is 11 or 12 ? 0.3 : 0);
        var noise = 0.75 + (Hash(account, brand, d) % 50) / 100.0;
        return Math.Round(baseMonthly / 26m * (decimal)(seasonal * noise), 2);
    }

    private static int Hash(string a, string b, DateTime d)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in a + "|" + b) h = h * 31 + c;
            h = h * 31 + d.Year * 400 + d.DayOfYear;
            return Math.Abs(h);
        }
    }

    public Task<BudgetFieldMap> GetBudgetFieldMapAsync(CancellationToken ct) =>
        Task.FromResult(new BudgetFieldMap { Notes = ["Mock mode — default Service Layer field names shown."] });

    public Task<int?> FindScenarioAsync(string name, DateTime fiscalYearStart, CancellationToken ct)
    {
        lock (Gate)
        {
            var hit = Scenarios.FirstOrDefault(s => s.Value.Name == name && s.Value.Start == fiscalYearStart);
            return Task.FromResult(hit.Key == 0 ? (int?)null : hit.Key);
        }
    }

    public Task<(int Numerator, bool Created)> EnsureScenarioAsync(string name, DateTime fiscalYearStart, CancellationToken ct)
    {
        lock (Gate)
        {
            var hit = Scenarios.FirstOrDefault(s => s.Value.Name == name && s.Value.Start == fiscalYearStart);
            if (hit.Key != 0) return Task.FromResult((hit.Key, false));
            var id = Scenarios.Keys.Max() + 1;
            Scenarios[id] = (name, fiscalYearStart);
            return Task.FromResult((id, true));
        }
    }

    public Task<UpsertResult> UpsertBudgetAsync(int scenario, string account, AccountKind kind, decimal[] periods, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Budgets.TryGetValue((scenario, account), out var cur))
            {
                if (cur.Periods.SequenceEqual(periods)) return Task.FromResult(new UpsertResult(PushAction.Unchanged, cur.Key.ToString()));
                Budgets[(scenario, account)] = (cur.Key, periods.ToArray());
                return Task.FromResult(new UpsertResult(PushAction.Updated, cur.Key.ToString()));
            }
            var key = Db.NextBudgetKey++;
            Budgets[(scenario, account)] = (key, periods.ToArray());
            return Task.FromResult(new UpsertResult(PushAction.Created, key.ToString()));
        }
    }

    public Task<bool> ClearBudgetAsync(int scenario, string account, AccountKind kind, CancellationToken ct)
    {
        lock (Gate)
        {
            if (!Budgets.TryGetValue((scenario, account), out var cur) || cur.Periods.All(p => p == 0)) return Task.FromResult(false);
            Budgets[(scenario, account)] = (cur.Key, new decimal[12]);
            return Task.FromResult(true);
        }
    }

    public Task<Dictionary<string, decimal[]>> ReadScenarioBudgetsAsync(int scenario, CancellationToken ct)
    {
        lock (Gate)
            return Task.FromResult(Budgets.Where(b => b.Key.Scenario == scenario).ToDictionary(b => b.Key.Account, b => b.Value.Periods.ToArray()));
    }

    public void Dispose() { }
}
