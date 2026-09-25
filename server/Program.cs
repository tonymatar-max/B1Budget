using B1Budget.Api.Api;
using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services;
using B1Budget.Api.Services.B1;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Run under the Windows Service Control Manager when launched as a service (no-op when run from a console),
// so `sc start` works and the service reports Running/Stopped correctly. UseWindowsService also points the
// content root at the executable's folder instead of C:\Windows\System32, which keeps `data` next to the app.
builder.Host.UseWindowsService(o => o.ServiceName = "Nexus B1 Budget");
builder.Logging.AddEventLog(o => o.SourceName = "Nexus B1 Budget");

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

// Enums travel as names ("Draft", "Revenue"), not ordinals, in both directions.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "budget.db")}"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<GatewayFactory>();
builder.Services.AddSingleton<PushService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddSingleton<MailQueue>();
builder.Services.AddHostedService<MailSender>();
builder.Services.AddHttpContextAccessor();

// Cookie sign-in for the SPA: HttpOnly + SameSite=Strict (the UI is served from the same origin), and plain
// 401/403 responses instead of redirects to a login page.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "nexus_budget_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5175").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    SchemaUpgrader.CreateMissingTables(db);
    // No migrations (EnsureCreated only builds a brand-new DB) — bring an existing DB forward column by column.
    void AddColumn(string table, string column, string ddl)
    {
        var exists = db.Database.SqlQueryRaw<int>(
            $"select count(*) as [Value] from pragma_table_info('{table}') where name = '{column}'").AsEnumerable().Single() == 1;
        if (!exists) db.Database.ExecuteSqlRaw($"alter table {table} add column {column} {ddl}");
    }
    AddColumn("Versions", "FamilyId", "INTEGER NOT NULL DEFAULT 0");
    AddColumn("Versions", "RevisionNo", "INTEGER NOT NULL DEFAULT 1");
    AddColumn("Versions", "ParentVersionId", "INTEGER NULL");
    AddColumn("Versions", "SupersededAt", "TEXT NULL");
    AddColumn("Settings", "PushMainBudget", "INTEGER NOT NULL DEFAULT 1");
    AddColumn("Settings", "MainScenarioName", "TEXT NOT NULL DEFAULT 'Main Budget'");
    db.Database.ExecuteSqlRaw("update Versions set FamilyId = Id where FamilyId = 0");

    // Multi-company: the old single settings row becomes company #1; brands, accounts and budgets get a CompanyId.
    AddColumn("Settings", "Name", "TEXT NOT NULL DEFAULT 'Company'");
    AddColumn("Settings", "Currency", "TEXT NOT NULL DEFAULT ''");
    AddColumn("Settings", "GroupRate", "TEXT NOT NULL DEFAULT '1'");
    db.Database.ExecuteSqlRaw("update Settings set Name = CompanyDb where Name = 'Company' and CompanyDb <> '' and Mode = 1");
    AddColumn("Versions", "CompanyId", "INTEGER NOT NULL DEFAULT 0");
    db.Database.ExecuteSqlRaw("update Versions set CompanyId = (select min(Id) from Settings) where CompanyId = 0");
    foreach (var (table, columns, ddl) in new[]
    {
        ("Brands", "Code, Name, Active",
         "CREATE TABLE \"Brands\" (\"CompanyId\" INTEGER NOT NULL, \"Code\" TEXT NOT NULL, \"Name\" TEXT NOT NULL, \"Active\" INTEGER NOT NULL, CONSTRAINT \"PK_Brands\" PRIMARY KEY (\"CompanyId\", \"Code\"))"),
        ("Accounts", "Code, Name, Kind, DisplayCode",
         "CREATE TABLE \"Accounts\" (\"CompanyId\" INTEGER NOT NULL, \"Code\" TEXT NOT NULL, \"Name\" TEXT NOT NULL, \"Kind\" INTEGER NOT NULL, \"DisplayCode\" TEXT NULL, CONSTRAINT \"PK_Accounts\" PRIMARY KEY (\"CompanyId\", \"Code\"))"),
    })
    {
        // The primary key changes (Code → CompanyId + Code), which SQLite can only do by rebuilding the table.
        var hasCompany = db.Database.SqlQueryRaw<int>(
            $"select count(*) as [Value] from pragma_table_info('{table}') where name = 'CompanyId'").AsEnumerable().Single() == 1;
        if (hasCompany) continue;
        db.Database.ExecuteSqlRaw($"alter table {table} rename to {table}_old");
        db.Database.ExecuteSqlRaw(ddl);
        db.Database.ExecuteSqlRaw($"insert into {table} (CompanyId, {columns}) select (select min(Id) from Settings), {columns} from {table}_old");
        db.Database.ExecuteSqlRaw($"drop table {table}_old");
    }

    // A push that was running when the service stopped will never finish — close it off.
    foreach (var run in db.PushRuns.Where(r => r.Status == PushStatus.Running))
    {
        run.Status = PushStatus.Failed;
        run.FinishedAt = DateTime.UtcNow;
        run.Message = "Interrupted — the service stopped while this push was running. Push again; it is safe to repeat.";
    }
    db.SaveChanges();

    // Fresh install: one demo company in mock mode, with demo master data, so the app is usable immediately.
    if (!db.Companies.Any())
    {
        db.Companies.Add(new Company { Name = "Demo company", Currency = "USD" });
        db.SaveChanges();
    }
    foreach (var company in db.Companies.Where(c => c.Mode == B1Mode.Mock).ToList())
    {
        if (db.Brands.Any(b => b.CompanyId == company.Id)) continue;
        await MasterData.LoadAsync(db, company, new MockGateway(company.Id), default);
    }
}

// Business-rule and B1 errors come back as { message } with a 400/404/502 instead of a stack trace.
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex switch
    {
        KeyNotFoundException => 404,
        UnauthorizedAccessException => 401,
        ForbiddenException => 403,
        InvalidOperationException or ArgumentException => 400,
        ServiceLayerException or HttpRequestException => 502,
        _ => 500,
    };
    await ctx.Response.WriteAsJsonAsync(new { message = ex is UnauthorizedAccessException ? "Please sign in." : ex?.Message ?? "Unexpected error." });
}));

app.UseCors();
app.UseDefaultFiles();
// index.html must never be cached, or browsers keep loading the previous build's hashed bundle after an upgrade.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    },
});
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapMail();
app.MapAll();
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
});

app.Run();
