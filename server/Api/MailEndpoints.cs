using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Api;

public record MailSettingsDto(bool Enabled, string Host, int Port, MailSecurity Security, string? UserName, bool HasPassword,
    string FromAddress, string FromName, string AppUrl);
public record SaveMailSettingsRequest(bool Enabled, string Host, int Port, MailSecurity Security, string? UserName, string? Password,
    bool ClearPassword, string FromAddress, string FromName, string AppUrl);
public record TestMailRequest(string To);

/// <summary>SMTP settings, test message and delivery log — administrators only.</summary>
public static class MailEndpoints
{
    public static void MapMail(this WebApplication app)
    {
        var g = app.MapGroup("/api/mail").RequireAuthorization();

        g.MapGet("/settings", async (AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            return ToDto(await GetAsync(db));
        });

        g.MapPut("/settings", async (SaveMailSettingsRequest r, AppDbContext db, AccessService access, SecretProtector secrets) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            if (r.Enabled)
            {
                if (string.IsNullOrWhiteSpace(r.Host)) return Results.BadRequest(new { message = "SMTP server is required." });
                if (r.Port is < 1 or > 65535) return Results.BadRequest(new { message = "Port must be 1–65535." });
                if (!r.FromAddress.Contains('@')) return Results.BadRequest(new { message = "Enter a valid sender address." });
                if (!Uri.TryCreate(r.AppUrl, UriKind.Absolute, out _))
                    return Results.BadRequest(new { message = "App address must be a full URL, e.g. http://budget.company.local:5140" });
            }
            var s = await GetAsync(db);
            s.Enabled = r.Enabled; s.Host = r.Host.Trim(); s.Port = r.Port; s.Security = r.Security;
            s.UserName = string.IsNullOrWhiteSpace(r.UserName) ? null : r.UserName.Trim();
            if (r.ClearPassword) s.PasswordEncrypted = null;
            else if (!string.IsNullOrEmpty(r.Password)) s.PasswordEncrypted = secrets.Protect(r.Password);
            s.FromAddress = r.FromAddress.Trim();
            s.FromName = string.IsNullOrWhiteSpace(r.FromName) ? "Nexus B1 Budget" : r.FromName.Trim();
            s.AppUrl = r.AppUrl.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        });

        // Sent synchronously so the admin sees the SMTP error right away.
        g.MapPost("/test", async (TestMailRequest r, AppDbContext db, AccessService access, SecretProtector secrets, CancellationToken ct) =>
        {
            var scope = await access.ScopeAsync();
            scope.RequireAdmin();
            var s = await GetAsync(db);
            if (string.IsNullOrWhiteSpace(s.Host)) return Results.BadRequest(new { message = "Save the SMTP settings first." });
            if (string.IsNullOrWhiteSpace(r.To) || !r.To.Contains('@')) return Results.BadRequest(new { message = "Enter the address to send the test to." });
            var html = Notifier.Template("Test message",
                $"This is a test from Nexus B1 Budget, sent by {System.Net.WebUtility.HtmlEncode(scope.User.DisplayName)}. If you can read it, notifications will be delivered.",
                $"SMTP {s.Host}:{s.Port}", null, "Open Nexus B1 Budget", s.AppUrl);
            var mail = new OutgoingMail(r.To.Trim(), null, "Nexus B1 Budget — test e-mail", html);
            var entry = new MailLog { To = mail.To, Subject = mail.Subject };
            try
            {
                await Smtp.SendAsync(s, s.PasswordEncrypted is null ? null : secrets.Unprotect(s.PasswordEncrypted), mail, ct);
                entry.Status = MailStatus.Sent;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entry.Status = MailStatus.Failed;
                entry.Error = ex.Message;
            }
            db.MailLogs.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry.Status == MailStatus.Sent
                ? Results.Ok(new { message = $"Test e-mail sent to {mail.To}." })
                : Results.BadRequest(new { message = "Sending failed: " + entry.Error });
        });

        g.MapGet("/log", async (AppDbContext db, AccessService access) =>
        {
            (await access.ScopeAsync()).RequireAdmin();
            return await db.MailLogs.AsNoTracking().OrderByDescending(m => m.Id).Take(100).ToListAsync();
        });
    }

    private static async Task<MailSettings> GetAsync(AppDbContext db)
    {
        var s = await db.MailSettings.FindAsync(1);
        if (s is null)
        {
            s = new MailSettings();
            db.MailSettings.Add(s);
            await db.SaveChangesAsync();
        }
        return s;
    }

    private static MailSettingsDto ToDto(MailSettings s) => new(s.Enabled, s.Host, s.Port, s.Security, s.UserName,
        !string.IsNullOrEmpty(s.PasswordEncrypted), s.FromAddress, s.FromName, s.AppUrl);
}
