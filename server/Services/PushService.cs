using System.Text.RegularExpressions;
using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

/// <summary>
/// Pushes an approved budget revision into native SAP B1 budgets:
/// <list type="bullet">
/// <item>one budget scenario per brand (named by the settings pattern), one B1 budget per G/L account in it;</item>
/// <item>the fiscal year's main scenario ("Main Budget") gets the all-brand total per account.</item>
/// </list>
/// The push is a sync: anything this app pushed earlier for the same fiscal year that the revision no
/// longer contains is zeroed ("Cleared"). Budgets entered manually in B1 are never touched.
/// Runs in the background; progress is persisted as PushItems so the UI can poll.
/// </summary>
public partial class PushService(IServiceScopeFactory scopes, ILogger<PushService> log)
{
    public const string AllBrands = "*";
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public static string ScenarioName(string pattern, string brand, int year) =>
        pattern.Replace("{brand}", brand, StringComparison.OrdinalIgnoreCase)
               .Replace("{year}", year.ToString(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s\(#\d+\)$")]
    private static partial Regex ScenarioIdSuffix();

    /// <summary>PushItem.Scenario is stored as "NIKE 2026 (#12)" — the plain scenario name.</summary>
    public static string BaseScenario(string stored) => ScenarioIdSuffix().Replace(stored, "");

    public async Task<int> StartAsync(int versionId, IReadOnlyCollection<string>? brands, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var version = await db.Versions.FindAsync([versionId], ct) ?? throw new KeyNotFoundException("Budget version not found.");
        if (version.Status == VersionStatus.Draft)
            throw new InvalidOperationException("Approve the budget before pushing it to SAP B1.");
        if (version.Status == VersionStatus.Superseded)
            throw new InvalidOperationException("This revision has been superseded — push the latest approved revision instead.");

        var company = await db.Companies.FindAsync([version.CompanyId], ct) ?? throw new KeyNotFoundException("The budget's company no longer exists.");
        var knownAccounts = (await db.Accounts.Where(a => a.CompanyId == company.Id).Select(a => a.Code).ToListAsync(ct)).ToHashSet();
        var orphans = (await db.Lines.Where(l => l.VersionId == versionId).Select(l => l.AccountCode).Distinct().ToListAsync(ct))
            .Where(a => !knownAccounts.Contains(a)).ToList();
        if (orphans.Count > 0)
            throw new InvalidOperationException(
                $"{orphans.Count} account(s) in this budget are not P&L accounts in SAP B1 ({string.Join(", ", orphans.Take(5))}). Create a revision and remove them first.");
        if (await db.PushRuns.AnyAsync(r => r.Status == PushStatus.Running, ct))
            throw new InvalidOperationException("Another push is already running.");

        var run = new PushRun { VersionId = versionId, Mode = company.Mode };
        db.PushRuns.Add(run);
        await db.SaveChangesAsync(ct);

        _ = Task.Run(() => ExecuteAsync(run.Id, brands), CancellationToken.None);
        return run.Id;
    }

    /// <summary>One B1 scenario and the per-account 12-period amounts it should end up holding.</summary>
    private record Target(string Brand, string Scenario, Dictionary<string, decimal[]> Accounts);

    private async Task ExecuteAsync(int runId, IReadOnlyCollection<string>? brandFilter)
    {
        await _oneAtATime.WaitAsync();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<GatewayFactory>();
        var run = await db.PushRuns.FirstAsync(r => r.Id == runId);
        try
        {
            var version = await db.Versions.Include(v => v.Lines).FirstAsync(v => v.Id == run.VersionId);
            var settings = await db.Companies.FirstAsync(c => c.Id == version.CompanyId);
            using var gateway = factory.Create(settings);
            var cal = new FiscalCalendar(settings.FiscalYearStartMonth);
            var year = version.FiscalYear;
            var kinds = await db.AccountKindsAsync(settings.Id);
            var filtered = brandFilter is { Count: > 0 };

            // ---- what each scenario should hold
            var targets = version.Lines
                .Where(l => !filtered || brandFilter!.Contains(l.BrandCode))
                .GroupBy(l => l.BrandCode).OrderBy(g => g.Key)
                .Select(g => new Target(g.Key, ScenarioName(settings.ScenarioNamePattern, g.Key, year),
                    g.ToDictionary(l => l.AccountCode, l => l.Amounts)))
                .ToList();

            // Brands this app pushed before for this year but which the revision no longer has → clear them too.
            var history = await PreviouslyPushedAsync(db, settings.Id, year, settings.Mode);
            if (!filtered)
            {
                var mainName = settings.MainScenarioName;
                foreach (var scen in history.Keys.Where(k => k.Scenario != mainName && targets.All(t => t.Scenario != k.Scenario)).Select(k => (k.Brand, k.Scenario)).Distinct())
                    targets.Add(new Target(scen.Brand, scen.Scenario, new()));
            }

            // Main budget = all brands of the revision summed per account (always the whole revision, even for a partial push).
            if (settings.PushMainBudget && !string.IsNullOrWhiteSpace(settings.MainScenarioName))
            {
                var total = new Dictionary<string, decimal[]>();
                foreach (var l in version.Lines)
                {
                    if (!total.TryGetValue(l.AccountCode, out var arr)) total[l.AccountCode] = arr = new decimal[12];
                    for (var i = 0; i < 12; i++) arr[i] += l.Amounts[i];
                }
                targets.Add(new Target(AllBrands, settings.MainScenarioName, total));
            }

            foreach (var t in targets)
            {
                int scenario;
                try
                {
                    if (t.Accounts.Count == 0)
                    {
                        // Only clearing — never create a scenario just to zero it.
                        if (await gateway.FindScenarioAsync(t.Scenario, cal.YearStart(year), CancellationToken.None) is not int found) continue;
                        scenario = found;
                    }
                    else
                    {
                        // Stamp the scenario with its cost center (the brand's distribution rule). The Main Budget spans
                        // every cost center, so it is created without one.
                        var costCenter = t.Brand == AllBrands ? null : t.Brand;
                        (scenario, var created) = await gateway.EnsureScenarioAsync(t.Scenario, cal.YearStart(year), costCenter, CancellationToken.None);
                        if (created) log.LogInformation("Created budget scenario {Name} ({Id})", t.Scenario, scenario);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var (acct, amounts) in t.Accounts)
                        run.Items.Add(new PushItem
                        {
                            BrandCode = t.Brand, AccountCode = acct, Scenario = t.Scenario,
                            Action = PushAction.Failed, Annual = amounts.Sum(), Error = "Scenario: " + ex.Message,
                        });
                    await db.SaveChangesAsync();
                    continue;
                }

                foreach (var (acct, amounts) in t.Accounts.OrderBy(a => a.Key))
                {
                    var item = new PushItem { BrandCode = t.Brand, AccountCode = acct, Scenario = $"{t.Scenario} (#{scenario})", Annual = amounts.Sum() };
                    try
                    {
                        var res = await gateway.UpsertBudgetAsync(scenario, acct, kinds.GetValueOrDefault(acct, AccountKind.Expense), amounts, CancellationToken.None);
                        item.Action = res.Action;
                        item.B1Key = res.Key;
                    }
                    catch (Exception ex) { item.Action = PushAction.Failed; item.Error = ex.Message; }
                    run.Items.Add(item);
                    await db.SaveChangesAsync();   // persist per line so the UI can show live progress
                }

                // Accounts pushed to this scenario before that the revision dropped → zero them in B1.
                var stale = history.Where(h => h.Key.Scenario == t.Scenario).SelectMany(h => h.Value).Where(a => !t.Accounts.ContainsKey(a)).Distinct();
                foreach (var acct in stale.OrderBy(a => a))
                {
                    try
                    {
                        if (!await gateway.ClearBudgetAsync(scenario, acct, kinds.GetValueOrDefault(acct, AccountKind.Expense), CancellationToken.None)) continue;
                        run.Items.Add(new PushItem { BrandCode = t.Brand, AccountCode = acct, Scenario = $"{t.Scenario} (#{scenario})", Action = PushAction.Cleared });
                    }
                    catch (Exception ex)
                    {
                        run.Items.Add(new PushItem { BrandCode = t.Brand, AccountCode = acct, Scenario = $"{t.Scenario} (#{scenario})", Action = PushAction.Failed, Error = "Clearing: " + ex.Message });
                    }
                    await db.SaveChangesAsync();
                }
            }

            var failed = run.Items.Count(i => i.Action == PushAction.Failed);
            run.Status = failed == 0 ? PushStatus.Succeeded : failed == run.Items.Count ? PushStatus.Failed : PushStatus.PartiallyFailed;
            run.Message = $"{Count(run, PushAction.Created)} created, {Count(run, PushAction.Updated)} updated, " +
                          $"{Count(run, PushAction.Unchanged)} unchanged, {Count(run, PushAction.Cleared)} cleared, {failed} failed.";
            if (run.Status != PushStatus.Failed)
            {
                version.Status = VersionStatus.Pushed;
                version.LastPushedAt = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Push run {Run} failed", runId);
            run.Status = PushStatus.Failed;
            run.Message = ex.Message;
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _oneAtATime.Release();
        }
    }

    private static int Count(PushRun r, PushAction a) => r.Items.Count(i => i.Action == a);

    /// <summary>(brand, scenario) → accounts this app has successfully pushed there for this fiscal year.</summary>
    private static async Task<Dictionary<(string Brand, string Scenario), HashSet<string>>> PreviouslyPushedAsync(AppDbContext db, int companyId, int year, B1Mode mode)
    {
        var rows = await (from i in db.PushItems
                          join r in db.PushRuns on i.RunId equals r.Id
                          join v in db.Versions on r.VersionId equals v.Id
                          where v.CompanyId == companyId && v.FiscalYear == year && r.Mode == mode && i.Action != PushAction.Failed && i.Action != PushAction.Cleared
                          select new { i.BrandCode, i.Scenario, i.AccountCode }).ToListAsync();
        return rows.GroupBy(x => (x.BrandCode, BaseScenario(x.Scenario)))
                   .ToDictionary(g => g.Key, g => g.Select(x => x.AccountCode).ToHashSet());
    }
}
