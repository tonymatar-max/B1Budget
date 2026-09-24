using System.Security.Claims;
using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

/// <summary>Thrown when a signed-in user tries something outside their role or departments → HTTP 403.</summary>
public class ForbiddenException(string message) : Exception(message);

/// <summary>
/// What the signed-in user may see: admins see every company and cost center; everyone else sees their own
/// departments plus, for managers, the departments of everyone who reports to them (directly or indirectly).
/// </summary>
public class Scope
{
    public required AppUser User { get; init; }
    public bool IsAdmin => User.Role == UserRole.Admin;
    public required HashSet<(int CompanyId, string Brand)> Departments { get; init; }
    /// <summary>User ids whose submissions this user approves (direct and indirect reports).</summary>
    public required HashSet<int> Team { get; init; }

    public bool Has(int companyId, string brand) => IsAdmin || Departments.Contains((companyId, brand));
    public bool HasCompany(int companyId) => IsAdmin || Departments.Any(d => d.CompanyId == companyId);
    public HashSet<string> BrandsIn(int companyId) => Departments.Where(d => d.CompanyId == companyId).Select(d => d.Brand).ToHashSet();

    public void RequireAdmin()
    {
        if (!IsAdmin) throw new ForbiddenException("Only an administrator can do this.");
    }

    public void Require(int companyId, string brand)
    {
        if (!Has(companyId, brand)) throw new ForbiddenException($"You don't have access to cost center {brand}.");
    }
}

public class AccessService(AppDbContext db, IHttpContextAccessor http)
{
    private Scope? _scope;

    public static int? UserIdOf(ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public async Task<Scope> ScopeAsync()
    {
        if (_scope != null) return _scope;
        var id = UserIdOf(http.HttpContext!.User) ?? throw new UnauthorizedAccessException();
        var user = await db.Users.Include(u => u.Departments).AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.Active)
                   ?? throw new UnauthorizedAccessException();

        // Everyone under this user in the reporting line (guarding against manager loops).
        var all = await db.Users.AsNoTracking().Select(u => new { u.Id, u.ManagerId }).ToListAsync();
        var team = new HashSet<int>();
        var queue = new Queue<int>([id]);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            foreach (var u in all.Where(x => x.ManagerId == m && x.Id != id))
                if (team.Add(u.Id)) queue.Enqueue(u.Id);
        }

        var depts = await db.UserDepartments.AsNoTracking()
            .Where(d => d.UserId == id || team.Contains(d.UserId))
            .Select(d => new { d.CompanyId, d.BrandCode }).ToListAsync();
        return _scope = new Scope
        {
            User = user,
            Team = team,
            Departments = depts.Select(d => (d.CompanyId, d.BrandCode)).ToHashSet(),
        };
    }

    /// <summary>The company a request works in (X-Company-Id header), limited to companies the user can access.</summary>
    public async Task<Company> CompanyAsync()
    {
        var scope = await ScopeAsync();
        var ctx = http.HttpContext!;
        if (int.TryParse(ctx.Request.Headers["X-Company-Id"], out var id) && scope.HasCompany(id) && await db.Companies.FindAsync(id) is { } c)
            return c;
        var allowed = scope.IsAdmin ? null : scope.Departments.Select(d => d.CompanyId).ToHashSet();
        return await db.Companies.Where(x => allowed == null || allowed.Contains(x.Id)).OrderBy(x => x.Id).FirstOrDefaultAsync()
               ?? throw new ForbiddenException(scope.IsAdmin
                   ? "No company is configured — add one under Companies."
                   : "You are not linked to any department yet — ask an administrator to assign you one.");
    }

    /// <summary>Load a version the user may see (their company), or throw 404/403.</summary>
    public async Task<BudgetVersion> VersionAsync(int id, bool withLines = false, bool track = false)
    {
        var q = db.Versions.AsQueryable();
        if (withLines) q = q.Include(v => v.Lines);
        if (!track) q = q.AsNoTracking();
        var v = await q.FirstOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException("Budget version not found.");
        if (!(await ScopeAsync()).HasCompany(v.CompanyId)) throw new ForbiddenException("You don't have access to this budget.");
        return v;
    }
}
