using System.Globalization;
using B1Budget.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace B1Budget.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<GlAccount> Accounts => Set<GlAccount>();
    public DbSet<BudgetVersion> Versions => Set<BudgetVersion>();
    public DbSet<BudgetLine> Lines => Set<BudgetLine>();
    public DbSet<PushRun> PushRuns => Set<PushRun>();
    public DbSet<PushItem> PushItems => Set<PushItem>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<DepartmentStatus> DepartmentStatuses => Set<DepartmentStatus>();
    public DbSet<DepartmentEvent> DepartmentEvents => Set<DepartmentEvent>();
    public DbSet<MailSettings> MailSettings => Set<MailSettings>();
    public DbSet<MailLog> MailLogs => Set<MailLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Company>().ToTable("Settings").HasKey(x => x.Id);
        b.Entity<Brand>().HasKey(x => new { x.CompanyId, x.Code });
        b.Entity<GlAccount>().HasKey(x => new { x.CompanyId, x.Code });
        b.Entity<BudgetVersion>().HasIndex(v => v.CompanyId);
        b.Entity<AppUser>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<MailSettings>().Property(m => m.Id).ValueGeneratedNever();
        b.Entity<MailLog>().HasIndex(m => m.At);
        b.Entity<AppUser>().HasMany(u => u.Departments).WithOne().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserDepartment>().HasIndex(d => new { d.UserId, d.CompanyId, d.BrandCode }).IsUnique();
        b.Entity<DepartmentStatus>().HasIndex(d => new { d.VersionId, d.BrandCode }).IsUnique();
        b.Entity<DepartmentEvent>().HasIndex(d => new { d.VersionId, d.BrandCode });

        b.Entity<BudgetVersion>().HasMany(v => v.Lines).WithOne().HasForeignKey(l => l.VersionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BudgetLine>().HasIndex(l => new { l.VersionId, l.BrandCode, l.AccountCode }).IsUnique();

        // 12 period amounts stored as one invariant-culture string ("100;0;250.5;…").
        var amountsConverter = new ValueConverter<decimal[], string>(
            v => string.Join(';', v.Select(d => d.ToString(CultureInfo.InvariantCulture))),
            s => ParseAmounts(s));
        var amountsComparer = new ValueComparer<decimal[]>(
            (a, c) => a!.SequenceEqual(c!), a => a.Aggregate(0, (h, d) => HashCode.Combine(h, d)), a => a.ToArray());
        b.Entity<BudgetLine>().Property(l => l.Amounts).HasConversion(amountsConverter, amountsComparer);

        b.Entity<PushRun>().HasMany(r => r.Items).WithOne().HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Cascade);

        // SQLite drops DateTimeKind — mark everything read back as UTC so the browser doesn't treat it as local.
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var p in et.GetProperties())
            {
                if (p.ClrType == typeof(DateTime)) p.SetValueConverter(utc);
                else if (p.ClrType == typeof(DateTime?)) p.SetValueConverter(utcNullable);
            }
    }

    private static decimal[] ParseAmounts(string s)
    {
        var result = new decimal[12];
        if (string.IsNullOrEmpty(s)) return result;
        var parts = s.Split(';');
        for (var i = 0; i < Math.Min(12, parts.Length); i++)
            decimal.TryParse(parts[i], NumberStyles.Number, CultureInfo.InvariantCulture, out result[i]);
        return result;
    }

    public async Task<Company> CompanyOfVersionAsync(int versionId)
    {
        var v = await Versions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId) ?? throw new KeyNotFoundException("Budget version not found.");
        return await Companies.FindAsync(v.CompanyId) ?? throw new KeyNotFoundException("The budget's company no longer exists.");
    }

    public Task<Dictionary<string, AccountKind>> AccountKindsAsync(int companyId) =>
        Accounts.Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Code, a => a.Kind);
}
