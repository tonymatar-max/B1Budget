using System.Security.Claims;
using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Api;

public record LoginRequest(string UserName, string Password);
public record SetupRequest(string UserName, string DisplayName, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record DepartmentRef(int CompanyId, string BrandCode);
public record SaveUserRequest(string UserName, string DisplayName, string? Email, UserRole Role, int? ManagerId, bool Active,
    string? Password, List<DepartmentRef> Departments);

public static class AuthEndpoints
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    public const int MinPasswordLength = 8;

    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        // Anonymous: tells the SPA whether to show first-run setup, the login form, or the app.
        auth.MapGet("/state", async (HttpContext http, AppDbContext db) =>
        {
            if (!await db.Users.AnyAsync()) return Results.Ok(new { needsSetup = true, user = (object?)null });
            var id = AccessService.UserIdOf(http.User);
            var user = id is null ? null : await db.Users.Include(u => u.Departments).AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.Active);
            return Results.Ok(new { needsSetup = false, user = user is null ? null : Me(user) });
        }).AllowAnonymous();

        // First run only: create the first administrator and sign them in.
        auth.MapPost("/setup", async (SetupRequest r, HttpContext http, AppDbContext db) =>
        {
            if (await db.Users.AnyAsync()) return Results.BadRequest(new { message = "Setup has already been completed." });
            // Whoever completes setup becomes administrator — only allow it from the server machine itself.
            if (http.Connection.RemoteIpAddress is { } ip && !System.Net.IPAddress.IsLoopback(ip))
                return Results.BadRequest(new { message = "Create the first administrator from a browser on the server itself (http://localhost)." });
            if (PasswordProblem(r.Password) is { } p) return Results.BadRequest(new { message = p });
            if (string.IsNullOrWhiteSpace(r.UserName)) return Results.BadRequest(new { message = "User name is required." });
            var user = new AppUser
            {
                UserName = r.UserName.Trim().ToLowerInvariant(), DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.UserName.Trim() : r.DisplayName.Trim(),
                Role = UserRole.Admin, LastLoginAt = DateTime.UtcNow,
            };
            user.PasswordHash = Hasher.HashPassword(user, r.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            await SignInAsync(http, user);
            return Results.Ok(Me(user));
        }).AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest r, HttpContext http, AppDbContext db) =>
        {
            var name = (r.UserName ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.Include(u => u.Departments).FirstOrDefaultAsync(u => u.UserName == name);
            var ok = user is { Active: true } && Hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password ?? "") != PasswordVerificationResult.Failed;
            if (!ok)
            {
                await Task.Delay(600);   // blunt online guessing
                return Results.BadRequest(new { message = "Wrong user name or password." });
            }
            user!.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SignInAsync(http, user);
            return Results.Ok(Me(user));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).AllowAnonymous();

        auth.MapPost("/password", async (ChangePasswordRequest r, HttpContext http, AppDbContext db) =>
        {
            var id = AccessService.UserIdOf(http.User) ?? throw new UnauthorizedAccessException();
            var user = await db.Users.FirstAsync(u => u.Id == id);
            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, r.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { message = "Current password is wrong." });
            if (PasswordProblem(r.NewPassword) is { } p) return Results.BadRequest(new { message = p });
            user.PasswordHash = Hasher.HashPassword(user, r.NewPassword);
            user.MustChangePassword = false;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        // ------------------------------------------------------------ user administration (admins only)

        var users = app.MapGroup("/api/users").RequireAuthorization();

        users.MapGet("", async (AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var list = await db.Users.Include(u => u.Departments).AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync();
            return list.Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, u.Email, u.Role, u.ManagerId, u.Active, u.MustChangePassword, u.CreatedAt, u.LastLoginAt,
                Departments = u.Departments.Select(d => new DepartmentRef(d.CompanyId, d.BrandCode)),
            });
        });

        // Lightweight directory for pickers (managers, owners) — any signed-in user.
        users.MapGet("/directory", async (AppDbContext db) =>
            await db.Users.AsNoTracking().Where(u => u.Active).OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.DisplayName, u.Role }).ToListAsync());

        users.MapPost("", async (SaveUserRequest r, AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            var user = new AppUser { MustChangePassword = true };
            if (string.IsNullOrWhiteSpace(r.Password)) return Results.BadRequest(new { message = "Set an initial password for the new user." });
            if (await ApplyAsync(db, user, r) is { } error) return Results.BadRequest(new { message = error });
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(new { user.Id });
        });

        users.MapPut("/{id:int}", async (int id, SaveUserRequest r, AppDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync();
            scope.RequireAdmin();
            var user = await db.Users.Include(u => u.Departments).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound();
            if (id == scope.User.Id && (r.Role != UserRole.Admin || !r.Active))
                return Results.BadRequest(new { message = "You can't remove your own administrator access." });
            if (await ApplyAsync(db, user, r) is { } error) return Results.BadRequest(new { message = error });
            if (!string.IsNullOrWhiteSpace(r.Password)) user.MustChangePassword = true;   // admin reset → user picks their own
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }

    public static object Me(AppUser u) => new
    {
        u.Id, u.UserName, u.DisplayName, u.Email, u.Role, u.ManagerId, u.MustChangePassword,
        Departments = u.Departments.Select(d => new DepartmentRef(d.CompanyId, d.BrandCode)),
    };

    private static async Task SignInAsync(HttpContext http, AppUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static string? PasswordProblem(string? pw) =>
        string.IsNullOrEmpty(pw) || pw.Length < MinPasswordLength ? $"Password must be at least {MinPasswordLength} characters." : null;

    private static async Task<string?> ApplyAsync(AppDbContext db, AppUser user, SaveUserRequest r)
    {
        var name = (r.UserName ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0) return "User name is required.";
        if (await db.Users.AnyAsync(u => u.UserName == name && u.Id != user.Id)) return $"User name '{name}' is already taken.";
        if (!string.IsNullOrWhiteSpace(r.Password) && PasswordProblem(r.Password) is { } p) return p;

        // Manager must exist and must not create a loop (A → B → A).
        if (r.ManagerId is int m)
        {
            if (user.Id != 0 && m == user.Id) return "A user can't be their own manager.";
            var managers = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.ManagerId);
            if (!managers.ContainsKey(m)) return "Manager not found.";
            for (int? cur = m, guard = 0; cur is int c && guard < 100; cur = managers.GetValueOrDefault(c), guard++)
                if (user.Id != 0 && c == user.Id) return "That manager reports (directly or indirectly) to this user — it would create a loop.";
        }

        var companies = await db.Companies.Select(c => c.Id).ToListAsync();
        if (r.Departments.Any(d => !companies.Contains(d.CompanyId))) return "Unknown company in departments.";

        user.UserName = name;
        user.DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? name : r.DisplayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        user.Role = r.Role;
        user.ManagerId = r.ManagerId;
        user.Active = r.Active;
        if (!string.IsNullOrWhiteSpace(r.Password)) user.PasswordHash = Hasher.HashPassword(user, r.Password);
        user.Departments.RemoveAll(_ => true);
        user.Departments.AddRange(r.Departments.DistinctBy(d => (d.CompanyId, d.BrandCode))
            .Select(d => new UserDepartment { CompanyId = d.CompanyId, BrandCode = d.BrandCode }));
        return null;
    }
}
