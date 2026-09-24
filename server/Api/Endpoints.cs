using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Api;

public record CompanyDto(int Id, string Name, string Currency, decimal GroupRate, B1Mode Mode, string ServiceLayerUrl,
    string CompanyDb, string UserName, bool HasPassword, bool IgnoreSslErrors, int Dimension, int FiscalYearStartMonth,
    string ScenarioNamePattern, bool PushMainBudget, string MainScenarioName, string? FieldMapOverrideJson,
    DateTime? MasterDataSyncedAt, int BudgetCount);
public record SaveCompanyRequest(string Name, string Currency, decimal GroupRate, B1Mode Mode, string ServiceLayerUrl,
    string CompanyDb, string UserName, string? Password, bool IgnoreSslErrors, int Dimension, int FiscalYearStartMonth,
    string ScenarioNamePattern, bool PushMainBudget, string MainScenarioName, string? FieldMapOverrideJson);

public record VersionSummary(int Id, int CompanyId, int FamilyId, int RevisionNo, int? ParentVersionId, int FiscalYear, string Name, string? Notes,
    VersionStatus Status, DateTime CreatedAt, DateTime UpdatedAt, DateTime? ApprovedAt, DateTime? LastPushedAt, DateTime? SupersededAt,
    int LineCount, int BrandCount, decimal Revenue, decimal Expense);
public record ReviseRequest(string? Notes);
public record CreateVersionRequest(int FiscalYear, string Name, string? Notes, int? CopyFromVersionId);
public record UpdateVersionRequest(string Name, string? Notes);
public record LineDto(string BrandCode, string AccountCode, decimal[] Amounts);
public record SaveBrandLinesRequest(List<LineDto> Lines);
public record SeedRequest(int SourceFiscalYear, decimal UpliftPercent, List<string>? Brands, bool Overwrite);
public record PushRequest(List<string>? Brands);
public record DeptActionRequest(string? Comment);

public static class Endpoints
{
    public static void MapAll(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // ------------------------------------------------------------ companies & master data

        api.MapGet("/companies", async (AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var counts = await db.Versions.GroupBy(v => v.CompanyId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var list = (await db.Companies.OrderBy(c => c.Id).ToListAsync()).Where(c => scope.HasCompany(c.Id));
            // Connection details are for administrators only.
            return list.Select(c => scope.IsAdmin ? ToDto(c, counts.GetValueOrDefault(c.Id))
                : ToDto(c, counts.GetValueOrDefault(c.Id)) with { ServiceLayerUrl = "", CompanyDb = "", UserName = "", HasPassword = false, FieldMapOverrideJson = null });
        });

        api.MapPost("/companies", async (SaveCompanyRequest r, AppDbContext db, AccessService access, SecretProtector secrets) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = new Company();
            if (Apply(c, r, secrets) is { } error) return Results.BadRequest(new { message = error });
            db.Companies.Add(c);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(c, 0));
        });

        api.MapPut("/companies/{id:int}", async (int id, SaveCompanyRequest r, AppDbContext db, AccessService access, SecretProtector secrets) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await db.Companies.FindAsync(id);
            if (c is null) return Results.NotFound();
            var sourceChanged = c.Dimension != r.Dimension || c.Mode != r.Mode || c.CompanyDb != r.CompanyDb.Trim();
            if (Apply(c, r, secrets) is { } error) return Results.BadRequest(new { message = error });
            if (sourceChanged) c.MasterDataSyncedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(c, await db.Versions.CountAsync(v => v.CompanyId == id)));
        });

        api.MapDelete("/companies/{id:int}", async (int id, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await db.Companies.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (await db.Versions.AnyAsync(v => v.CompanyId == id))
                return Results.BadRequest(new { message = $"{c.Name} has budgets — it can only be removed while it has none." });
            if (await db.Companies.CountAsync() == 1) return Results.BadRequest(new { message = "At least one company is required." });
            await db.Brands.Where(b => b.CompanyId == id).ExecuteDeleteAsync();
            await db.Accounts.Where(a => a.CompanyId == id).ExecuteDeleteAsync();
            db.Companies.Remove(c);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapPost("/companies/{id:int}/test", async (int id, AppDbContext db, GatewayFactory f, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await db.Companies.FindAsync([id], ct) ?? throw new KeyNotFoundException("Company not found.");
            using var gw = f.Create(c);
            return Results.Ok(new { message = await gw.TestConnectionAsync(ct) });
        });

        api.MapGet("/companies/{id:int}/budget-fields", async (int id, AppDbContext db, GatewayFactory f, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await db.Companies.FindAsync([id], ct) ?? throw new KeyNotFoundException("Company not found.");
            using var gw = f.Create(c);
            return Results.Ok(await gw.GetBudgetFieldMapAsync(ct));
        });

        api.MapPost("/companies/{id:int}/sync", async (int id, AppDbContext db, GatewayFactory f, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await db.Companies.FindAsync([id], ct) ?? throw new KeyNotFoundException("Company not found.");
            using var gw = f.Create(c);
            var (brands, accounts) = await MasterData.LoadAsync(db, c, gw, ct);
            return Results.Ok(new { brands, accounts });
        });

        // ?all=true lists every brand of the company (admins assigning departments); otherwise the user's own.
        api.MapGet("/brands", async (int? companyId, bool? all, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var cid = companyId ?? (await access.CompanyAsync()).Id;
            if (!scope.HasCompany(cid)) throw new ForbiddenException("You don't have access to this company.");
            var brands = await db.Brands.Where(b => b.CompanyId == cid).OrderBy(b => b.Code).ToListAsync();
            return all == true && scope.IsAdmin ? brands : brands.Where(b => scope.Has(cid, b.Code)).ToList();
        });
        api.MapGet("/accounts", async (AppDbContext db, AccessService access) =>
        {
            var c = await access.CompanyAsync();
            return await db.Accounts.Where(a => a.CompanyId == c.Id).OrderBy(a => a.Code).ToListAsync();
        });

        // ------------------------------------------------------------ versions

        api.MapGet("/versions", async (AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var c = await access.CompanyAsync();
            var versions = await db.Versions.Include(v => v.Lines).AsNoTracking().Where(v => v.CompanyId == c.Id)
                .OrderByDescending(v => v.FiscalYear).ThenByDescending(v => v.Id).ToListAsync();
            var kinds = await db.AccountKindsAsync(c.Id);
            return versions.Select(v => Summary(Scoped(v, scope), kinds));
        });

        api.MapGet("/versions/{id:int}", async (int id, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var v = Scoped(await access.VersionAsync(id, withLines: true), scope);
            var kinds = await db.AccountKindsAsync(v.CompanyId);
            return Results.Ok(new
            {
                version = Summary(v, kinds),
                lines = v.Lines.Select(l => new LineDto(l.BrandCode, l.AccountCode, l.Amounts)),
            });
        });

        api.MapPost("/versions", async (CreateVersionRequest r, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest(new { message = "Name is required." });
            var company = await access.CompanyAsync();
            var v = new BudgetVersion { CompanyId = company.Id, FiscalYear = r.FiscalYear, Name = r.Name.Trim(), Notes = r.Notes };
            if (r.CopyFromVersionId is int src)
            {
                var from = await db.Versions.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == src);
                if (from is null) return Results.BadRequest(new { message = "Source version not found." });
                v.Lines = from.Lines.Select(l => new BudgetLine { BrandCode = l.BrandCode, AccountCode = l.AccountCode, Amounts = l.Amounts.ToArray() }).ToList();
            }
            db.Versions.Add(v);
            await db.SaveChangesAsync();
            v.FamilyId = v.Id;          // a new budget starts its own revision family
            await db.SaveChangesAsync();
            return Results.Ok(new { v.Id });
        });

        api.MapPut("/versions/{id:int}", async (int id, UpdateVersionRequest r, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var v = await db.Versions.FindAsync(id);
            if (v is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(r.Name)) return Results.BadRequest(new { message = "Name is required." });
            // The name belongs to the whole budget (all revisions); notes are per revision.
            foreach (var rev in await db.Versions.Where(x => x.FamilyId == v.FamilyId).ToListAsync()) rev.Name = r.Name.Trim();
            v.Notes = r.Notes; v.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapDelete("/versions/{id:int}", async (int id, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var v = await db.Versions.FindAsync(id);
            if (v is null) return Results.NotFound();
            if (v.Status != VersionStatus.Draft)
                return Results.BadRequest(new { message = "Approved revisions are kept for history and cannot be deleted." });
            db.Versions.Remove(v);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapPost("/versions/{id:int}/status/{status}", async (int id, VersionStatus status, AppDbContext db, AccessService access,
            WorkflowService flow, Notifier notifier, ILogger<WorkflowService> log) =>
        {
            var scope = await access.ScopeAsync();
            scope.RequireAdmin();
            var v = await db.Versions.FindAsync(id);
            if (v is null) return Results.NotFound();
            // Approval is one-way: an approved revision is never edited again — changes go into a new revision.
            if (status != VersionStatus.Approved || v.Status != VersionStatus.Draft)
                return Results.BadRequest(new { message = "Only a draft can be approved. To change an approved budget, create a revision." });
            if (!await db.Lines.AnyAsync(l => l.VersionId == id))
                return Results.BadRequest(new { message = "The budget has no lines." });
            // Final approval needs every department's own approval first.
            var pending = await flow.PendingBrandsAsync(v);
            if (pending.Count > 0)
                return Results.BadRequest(new { message = $"{pending.Count} department(s) are not approved yet: {string.Join(", ", pending.Take(8))}{(pending.Count > 8 ? "…" : "")}." });
            var now = DateTime.UtcNow;
            v.Status = VersionStatus.Approved;
            v.ApprovedAt = now;
            foreach (var older in await db.Versions.Where(x => x.FamilyId == v.FamilyId && x.Id != v.Id &&
                         (x.Status == VersionStatus.Approved || x.Status == VersionStatus.Pushed)).ToListAsync())
            {
                older.Status = VersionStatus.Superseded;
                older.SupersededAt = now;
            }
            await db.SaveChangesAsync();
            try { await notifier.BudgetApprovedAsync(v, scope.User); }
            catch (Exception ex) { log.LogWarning(ex, "Could not queue budget-approved notifications"); }
            return Results.Ok();
        });

        // New revision = copy of the current approved revision, as a draft. One open draft per budget at a time.
        api.MapPost("/versions/{id:int}/revise", async (int id, ReviseRequest r, AppDbContext db, AccessService access, WorkflowService flow) =>
        {
            var scope = await access.ScopeAsync();
            scope.RequireAdmin();
            var src = await db.Versions.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (src is null) return Results.NotFound();
            if (src.Status is not (VersionStatus.Approved or VersionStatus.Pushed))
                return Results.BadRequest(new { message = "Only the current approved revision can be revised." });
            var family = await db.Versions.Where(x => x.FamilyId == src.FamilyId).ToListAsync();
            if (family.FirstOrDefault(x => x.Status == VersionStatus.Draft) is { } open)
                return Results.BadRequest(new { message = $"Revision {open.RevisionNo} is already open as a draft.", id = open.Id });
            var rev = new BudgetVersion
            {
                CompanyId = src.CompanyId, FamilyId = src.FamilyId, RevisionNo = family.Max(x => x.RevisionNo) + 1, ParentVersionId = src.Id,
                FiscalYear = src.FiscalYear, Name = src.Name, Notes = r.Notes,
                Lines = src.Lines.Select(l => new BudgetLine { BrandCode = l.BrandCode, AccountCode = l.AccountCode, Amounts = l.Amounts.ToArray() }).ToList(),
            };
            db.Versions.Add(rev);
            await db.SaveChangesAsync();
            await flow.CarryOverAsync(src, rev, scope.User.Id);
            return Results.Ok(new { rev.Id, rev.RevisionNo });
        });

        api.MapGet("/versions/{id:int}/revisions", async (int id, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var v = await access.VersionAsync(id);
            var kinds = await db.AccountKindsAsync(v.CompanyId);
            var family = await db.Versions.Include(x => x.Lines).AsNoTracking().Where(x => x.FamilyId == v.FamilyId)
                .OrderBy(x => x.RevisionNo).ToListAsync();
            return Results.Ok(family.Select(x => Summary(Scoped(x, scope), kinds)));
        });

        // Line-by-line difference between two versions (any two — typically consecutive revisions).
        api.MapGet("/compare", async (int a, int b, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var va = Scoped(await access.VersionAsync(a, withLines: true), scope);
            var vb = Scoped(await access.VersionAsync(b, withLines: true), scope);
            var kinds = await db.AccountKindsAsync(va.CompanyId);
            var la = va.Lines.ToDictionary(l => (l.BrandCode, l.AccountCode), l => l.Amounts);
            var lb = vb.Lines.ToDictionary(l => (l.BrandCode, l.AccountCode), l => l.Amounts);
            var rows = la.Keys.Union(lb.Keys).OrderBy(k => k.BrandCode).ThenBy(k => k.AccountCode).Select(k => new
            {
                brand = k.BrandCode, account = k.AccountCode,
                a = la.GetValueOrDefault(k) ?? new decimal[12],
                b = lb.GetValueOrDefault(k) ?? new decimal[12],
                change = !la.ContainsKey(k) ? "Added" : !lb.ContainsKey(k) ? "Removed"
                    : la[k].SequenceEqual(lb[k]) ? "Same" : "Changed",
            });
            return Results.Ok(new { a = Summary(va, kinds), b = Summary(vb, kinds), rows });
        });

        // Replace every line for one brand (the editor saves per brand).
        api.MapPut("/versions/{id:int}/brands/{brand}/lines", async (int id, string brand, SaveBrandLinesRequest r, AppDbContext db,
            AccessService access, WorkflowService flow) =>
        {
            var v = await access.VersionAsync(id, withLines: true, track: true);
            await flow.RequireEditableAsync(v, brand, await access.ScopeAsync());
            if (!await db.Brands.AnyAsync(b => b.CompanyId == v.CompanyId && b.Code == brand)) return Results.BadRequest(new { message = $"Unknown brand {brand}." });
            var accountCodes = (await db.Accounts.Where(a => a.CompanyId == v.CompanyId).Select(a => a.Code).ToListAsync()).ToHashSet();
            var bad = r.Lines.Where(l => !accountCodes.Contains(l.AccountCode)).Select(l => l.AccountCode).ToList();
            if (bad.Count > 0) return Results.BadRequest(new { message = "Unknown account(s): " + string.Join(", ", bad) });
            if (r.Lines.GroupBy(l => l.AccountCode).Any(g => g.Count() > 1)) return Results.BadRequest(new { message = "Duplicate account lines." });
            if (r.Lines.Any(l => l.Amounts.Length != 12)) return Results.BadRequest(new { message = "Each line needs 12 period amounts." });

            v.Lines.RemoveAll(l => l.BrandCode == brand);
            v.Lines.AddRange(r.Lines.Select(l => new BudgetLine
            {
                BrandCode = brand, AccountCode = l.AccountCode, Amounts = l.Amounts.Select(a => Math.Round(a, 2)).ToArray(),
            }));
            v.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Remove lines whose account is no longer a synced P&L account (e.g. balance-sheet accounts, or leftovers from mock data).
        api.MapDelete("/versions/{id:int}/orphan-lines", async (int id, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var v = await db.Versions.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
            if (v is null) return Results.NotFound();
            if (v.Status != VersionStatus.Draft) return Results.BadRequest(new { message = "Approved budgets are locked — create a revision to change it." });
            var accounts = (await db.Accounts.Where(a => a.CompanyId == v.CompanyId).Select(a => a.Code).ToListAsync()).ToHashSet();
            var brands = (await db.Brands.Where(b => b.CompanyId == v.CompanyId).Select(b => b.Code).ToListAsync()).ToHashSet();
            var removed = v.Lines.RemoveAll(l => !accounts.Contains(l.AccountCode) || !brands.Contains(l.BrandCode));
            v.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { removed });
        });

        api.MapPost("/versions/{id:int}/seed", async (int id, SeedRequest r, AppDbContext db, ReportService reports,
            AccessService access, WorkflowService flow, CancellationToken ct) =>
        {
            var v = await access.VersionAsync(id, withLines: true, track: true);
            if (v.Status != VersionStatus.Draft) return Results.BadRequest(new { message = "Approved budgets are locked — create a revision to change it." });
            // Only departments the user may edit right now (their own, not submitted/approved).
            var editable = await flow.EditableBrandsAsync(v, await access.ScopeAsync());
            var company = await db.Companies.FirstAsync(c => c.Id == v.CompanyId, ct);
            var actuals = await reports.ActualsByKeyAsync(company, r.SourceFiscalYear, ct);
            var knownBrands = (await db.Brands.Where(b => b.CompanyId == v.CompanyId).Select(b => b.Code).ToListAsync(ct)).ToHashSet();
            var factor = 1 + r.UpliftPercent / 100m;
            var added = 0; var replaced = 0;
            foreach (var ((brand, account), amounts) in actuals)
            {
                if (!knownBrands.Contains(brand) || !editable.Contains(brand)) continue;
                if (r.Brands is { Count: > 0 } && !r.Brands.Contains(brand)) continue;
                if (amounts.All(a => a == 0)) continue;
                var target = amounts.Select(a => Math.Round(Math.Max(0, a) * factor, 0)).ToArray();
                var existing = v.Lines.FirstOrDefault(l => l.BrandCode == brand && l.AccountCode == account);
                if (existing is null) { v.Lines.Add(new BudgetLine { BrandCode = brand, AccountCode = account, Amounts = target }); added++; }
                else if (r.Overwrite) { existing.Amounts = target; replaced++; }
            }
            v.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added, replaced });
        });

        api.MapGet("/versions/{id:int}/export", async (int id, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var v = Scoped(await access.VersionAsync(id, withLines: true), scope);
            var s = await db.Companies.FirstAsync(c => c.Id == v.CompanyId);
            var bytes = ExcelService.Export(v, (await db.Brands.Where(b => b.CompanyId == s.Id).ToListAsync()).Where(b => scope.Has(s.Id, b.Code)),
                await db.Accounts.Where(a => a.CompanyId == s.Id).ToListAsync(), new FiscalCalendar(s.FiscalYearStartMonth));
            var file = $"Budget_{string.Concat(s.Name.Where(char.IsLetterOrDigit))}_{v.FiscalYear}_{string.Concat(v.Name.Where(char.IsLetterOrDigit))}_Rev{v.RevisionNo}.xlsx";
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file);
        });

        api.MapPost("/versions/{id:int}/import", async (int id, HttpRequest req, AppDbContext db, AccessService access, WorkflowService flow) =>
        {
            var v = await access.VersionAsync(id, withLines: true, track: true);
            var editable = await flow.EditableBrandsAsync(v, await access.ScopeAsync());
            if (v.Status != VersionStatus.Draft) return Results.BadRequest(new { message = "Approved budgets are locked — create a revision to change it." });
            if (!req.HasFormContentType) return Results.BadRequest(new { message = "Expected a file upload." });
            var file = (await req.ReadFormAsync()).Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { message = "No file uploaded." });

            var brands = (await db.Brands.Where(b => b.CompanyId == v.CompanyId).Select(b => b.Code).ToListAsync()).ToHashSet();
            var accounts = (await db.Accounts.Where(a => a.CompanyId == v.CompanyId).Select(a => a.Code).ToListAsync()).ToHashSet();
            ExcelService.ImportResult result;
            await using (var s = file.OpenReadStream())
            {
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                try { result = ExcelService.Import(ms, brands, accounts); }
                catch (Exception ex) { return Results.BadRequest(new { message = "Could not read the workbook: " + ex.Message }); }
            }

            // Import replaces lines only for brands present in the file; other brands are untouched.
            var brandsInFile = result.Lines.Select(l => l.BrandCode).ToHashSet();
            var locked = brandsInFile.Where(b => !editable.Contains(b)).OrderBy(b => b).ToList();
            if (locked.Count > 0)
                return Results.BadRequest(new { message = $"The file contains cost centers you can't change now: {string.Join(", ", locked)}. Remove them from the file (or have them reopened) and import again." });
            v.Lines.RemoveAll(l => brandsInFile.Contains(l.BrandCode));
            v.Lines.AddRange(result.Lines);
            v.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { imported = result.Lines.Count, brands = brandsInFile.Count, errors = result.Errors });
        });

        // ------------------------------------------------------------ push to SAP B1

        api.MapPost("/versions/{id:int}/push", async (int id, PushRequest r, PushService push, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            return Results.Ok(new { runId = await push.StartAsync(id, r.Brands, ct) });
        });

        api.MapGet("/push-runs", async (int? versionId, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var c = await access.CompanyAsync();
            var versionIds = db.Versions.Where(v => v.CompanyId == c.Id).Select(v => v.Id);
            return await db.PushRuns.Where(p => versionIds.Contains(p.VersionId) && (versionId == null || p.VersionId == versionId))
                .OrderByDescending(p => p.Id).Take(50)
                .Select(p => new
                {
                    p.Id, p.VersionId, p.StartedAt, p.FinishedAt, p.Status, p.Mode, p.Message,
                    Total = p.Items.Count, Failed = p.Items.Count(i => i.Action == PushAction.Failed),
                }).ToListAsync();
        });

        api.MapGet("/push-runs/{id:int}", async (int id, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var run = await db.PushRuns.Include(p => p.Items).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (run is null) return Results.NotFound();
            var expected = await db.Lines.CountAsync(l => l.VersionId == run.VersionId);
            return Results.Ok(new { run, expected });
        });

        // Compare the version with what native B1 budgets currently hold.
        api.MapGet("/versions/{id:int}/reconcile", async (int id, AppDbContext db, GatewayFactory f, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var v = await db.Versions.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return Results.NotFound();
            var s = await db.Companies.FirstAsync(c => c.Id == v.CompanyId, ct);
            using var gw = f.Create(s);
            var cal = new FiscalCalendar(s.FiscalYearStartMonth);
            // What each B1 scenario should hold: one per brand, plus the all-brand total in the main budget.
            var targets = v.Lines.GroupBy(l => l.BrandCode).OrderBy(g => g.Key)
                .Select(g => (Brand: g.Key, Scenario: PushService.ScenarioName(s.ScenarioNamePattern, g.Key, v.FiscalYear),
                              Accounts: g.ToDictionary(l => l.AccountCode, l => l.Amounts)))
                .ToList();
            if (s.PushMainBudget)
            {
                var total = new Dictionary<string, decimal[]>();
                foreach (var l in v.Lines)
                {
                    if (!total.TryGetValue(l.AccountCode, out var arr)) total[l.AccountCode] = arr = new decimal[12];
                    for (var i = 0; i < 12; i++) arr[i] += l.Amounts[i];
                }
                targets.Add((PushService.AllBrands, s.MainScenarioName, total));
            }

            var rows = new List<object>();
            foreach (var t in targets)
            {
                Dictionary<string, decimal[]> inB1;
                try
                {
                    var scen = await gw.FindScenarioAsync(t.Scenario, cal.YearStart(v.FiscalYear), ct);
                    inB1 = scen is int n ? await gw.ReadScenarioBudgetsAsync(n, ct) : new();
                }
                catch (Exception ex) { rows.Add(new { brand = t.Brand, scenario = t.Scenario, error = ex.Message }); continue; }
                foreach (var (acct, amounts) in t.Accounts.OrderBy(a => a.Key))
                {
                    var b1 = inB1.GetValueOrDefault(acct);
                    rows.Add(new
                    {
                        brand = t.Brand, scenario = t.Scenario, account = acct,
                        app = amounts.Sum(), b1 = b1?.Sum(), match = b1 != null && b1.SequenceEqual(amounts),
                    });
                }
            }
            return Results.Ok(rows);
        });

        api.MapGet("/diagnostics/actuals", async (int? year, AppDbContext db, GatewayFactory f, AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var s = await access.CompanyAsync();
            using var gw = f.Create(s);
            if (gw is not B1Budget.Api.Services.B1.ServiceLayerGateway live)
                return Results.BadRequest(new { message = "Diagnostics need Service Layer mode." });
            var cal = new FiscalCalendar(s.FiscalYearStartMonth);
            var y = year ?? cal.FiscalYearOf(DateTime.UtcNow);
            return Results.Text((await live.DiagnoseAsync(s.Dimension, cal.YearStart(y), cal.PeriodEnd(y, 12), ct)).ToJsonString(), "application/json");
        });

        // ------------------------------------------------------------ budget vs actual

        api.MapGet("/reports/bva", async (int versionId, int? from, int? to, bool? refresh, int? compareVersionId, ReportService reports,
            AccessService access, CancellationToken ct) =>
        {
            var scope = await access.ScopeAsync();
            var v = await access.VersionAsync(versionId);
            if (compareVersionId is int cv) await access.VersionAsync(cv);
            var report = await reports.BudgetVsActualAsync(versionId, from, to, refresh ?? false, compareVersionId, ct);
            // Non-admins see only their departments (postings without a cost center are an admin concern).
            return Results.Ok(scope.IsAdmin ? report : report with { Rows = report.Rows.Where(r => scope.Has(v.CompanyId, r.Brand)).ToList() });
        });

        // versions=3:12,4:17 picks a specific revision per company (companyId:versionId); default is each company's latest approved.
        api.MapGet("/reports/group", async (int year, int? from, int? to, bool? refresh, string? versions, ReportService reports,
            AccessService access, CancellationToken ct) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var overrides = (versions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':')).Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                .ToDictionary(p => int.Parse(p[0]), p => int.Parse(p[1]));
            return Results.Ok(await reports.GroupAsync(year, from, to, refresh ?? false, overrides, ct));
        });

        api.MapGet("/reports/drill", async (int versionId, string? brand, string account, int from, int to, ReportService reports,
            AccessService access, CancellationToken ct) =>
        {
            var scope = await access.ScopeAsync();
            var v = await access.VersionAsync(versionId);
            if (string.IsNullOrEmpty(brand)) scope.RequireAdmin(); else scope.Require(v.CompanyId, brand);
            return Results.Ok(await reports.DrillAsync(versionId, brand ?? "", account, from, to, ct));
        });

        // ------------------------------------------------------------ department approval workflow

        api.MapGet("/versions/{id:int}/departments", async (int id, AccessService access, WorkflowService flow) =>
            Results.Ok(await flow.DepartmentsAsync(await access.VersionAsync(id), await access.ScopeAsync())));

        api.MapPost("/versions/{id:int}/departments/{brand}/{action}", async (int id, string brand, string action, DeptActionRequest r,
            AccessService access, WorkflowService flow) =>
        {
            var act = action.ToLowerInvariant() switch
            {
                "submit" => DeptAction.Submitted, "approve" => DeptAction.Approved,
                "reject" => DeptAction.Rejected, "reopen" => DeptAction.Reopened,
                _ => throw new ArgumentException($"Unknown action '{action}'."),
            };
            await flow.ActAsync(await access.VersionAsync(id), brand, act, r.Comment, await access.ScopeAsync());
            return Results.Ok();
        });

        api.MapGet("/versions/{id:int}/departments/{brand}/events", async (int id, string brand, AppDbContext db, AccessService access) =>
        {
            var v = await access.VersionAsync(id);
            (await access.ScopeAsync()).Require(v.CompanyId, brand);
            var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            return (await db.DepartmentEvents.AsNoTracking().Where(e => e.VersionId == id && e.BrandCode == brand).OrderByDescending(e => e.At).ToListAsync())
                .Select(e => new { e.Action, e.At, e.Comment, User = names.GetValueOrDefault(e.UserId, "?") });
        });

        // Submissions waiting for me (admins: every submission), across companies.
        api.MapGet("/approvals/inbox", async (AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            var me = scope.User.Id;
            var items = await (from s in db.DepartmentStatuses
                               join v in db.Versions on s.VersionId equals v.Id
                               join c in db.Companies on v.CompanyId equals c.Id
                               where s.Status == DeptStatus.Submitted && v.Status == VersionStatus.Draft
                                     && (scope.IsAdmin || s.ApproverId == me)
                               select new { s.VersionId, s.BrandCode, s.SubmittedById, s.SubmittedAt, s.Comment, s.ApproverId,
                                            v.CompanyId, CompanyName = c.Name, v.Name, v.RevisionNo, v.FiscalYear }).ToListAsync();
            var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
            return items.OrderBy(i => i.SubmittedAt).Select(i => new
            {
                i.VersionId, Brand = i.BrandCode, i.CompanyId, i.CompanyName, Budget = $"{i.Name} · Rev {i.RevisionNo}", i.FiscalYear,
                SubmittedBy = i.SubmittedById is int sb ? names.GetValueOrDefault(sb) : null, i.SubmittedAt, i.Comment,
                AssignedToMe = i.ApproverId == me,
            });
        });
    }

    /// <summary>Drop lines of departments the user can't see (no-op for admins).</summary>
    private static BudgetVersion Scoped(BudgetVersion v, Scope scope)
    {
        if (!scope.IsAdmin) v.Lines = v.Lines.Where(l => scope.Has(v.CompanyId, l.BrandCode)).ToList();
        return v;
    }

    private static CompanyDto ToDto(Company c, int budgetCount) => new(c.Id, c.Name, c.Currency, c.GroupRate, c.Mode,
        c.ServiceLayerUrl, c.CompanyDb, c.UserName, !string.IsNullOrEmpty(c.PasswordEncrypted), c.IgnoreSslErrors, c.Dimension,
        c.FiscalYearStartMonth, c.ScenarioNamePattern, c.PushMainBudget, c.MainScenarioName, c.FieldMapOverrideJson,
        c.MasterDataSyncedAt, budgetCount);

    /// <summary>Validate and copy a company form onto the entity; returns an error message or null.</summary>
    private static string? Apply(Company c, SaveCompanyRequest r, SecretProtector secrets)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Company name is required.";
        if (r.Dimension is < 1 or > 5) return "Dimension must be 1–5.";
        if (r.FiscalYearStartMonth is < 1 or > 12) return "Fiscal year start month must be 1–12.";
        if (r.GroupRate <= 0) return "Group rate must be greater than zero.";
        if (!r.ScenarioNamePattern.Contains("{brand}", StringComparison.OrdinalIgnoreCase)) return "Scenario name pattern must contain {brand}.";
        c.Name = r.Name.Trim(); c.Currency = r.Currency.Trim().ToUpperInvariant(); c.GroupRate = r.GroupRate;
        c.Mode = r.Mode; c.ServiceLayerUrl = r.ServiceLayerUrl.Trim(); c.CompanyDb = r.CompanyDb.Trim();
        c.UserName = r.UserName.Trim(); c.IgnoreSslErrors = r.IgnoreSslErrors; c.Dimension = r.Dimension;
        c.FiscalYearStartMonth = r.FiscalYearStartMonth; c.ScenarioNamePattern = r.ScenarioNamePattern.Trim();
        c.PushMainBudget = r.PushMainBudget;
        c.MainScenarioName = string.IsNullOrWhiteSpace(r.MainScenarioName) ? "Main Budget" : r.MainScenarioName.Trim();
        c.FieldMapOverrideJson = string.IsNullOrWhiteSpace(r.FieldMapOverrideJson) ? null : r.FieldMapOverrideJson;
        if (!string.IsNullOrEmpty(r.Password)) c.PasswordEncrypted = secrets.Protect(r.Password);
        return null;
    }

    private static VersionSummary Summary(BudgetVersion v, Dictionary<string, AccountKind> kinds) => new(
        v.Id, v.CompanyId, v.FamilyId, v.RevisionNo, v.ParentVersionId, v.FiscalYear, v.Name, v.Notes, v.Status, v.CreatedAt, v.UpdatedAt,
        v.ApprovedAt, v.LastPushedAt, v.SupersededAt,
        v.Lines.Count, v.Lines.Select(l => l.BrandCode).Distinct().Count(),
        v.Lines.Where(l => kinds.GetValueOrDefault(l.AccountCode) == AccountKind.Revenue).Sum(l => l.Amounts.Sum()),
        v.Lines.Where(l => kinds.GetValueOrDefault(l.AccountCode) != AccountKind.Revenue).Sum(l => l.Amounts.Sum()));
}
