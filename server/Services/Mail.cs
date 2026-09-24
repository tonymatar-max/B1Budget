using System.Net;
using System.Threading.Channels;
using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace B1Budget.Api.Services;

public record OutgoingMail(string To, string? ToName, string Subject, string Html);

/// <summary>In-memory queue so workflow actions never wait on (or fail because of) the mail server.</summary>
public class MailQueue
{
    private readonly Channel<OutgoingMail> _channel = Channel.CreateUnbounded<OutgoingMail>();
    public void Enqueue(OutgoingMail m) => _channel.Writer.TryWrite(m);
    public IAsyncEnumerable<OutgoingMail> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public static class Smtp
{
    public static async Task SendAsync(MailSettings s, string? password, OutgoingMail mail, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s.FromName, s.FromAddress));
        msg.To.Add(new MailboxAddress(mail.ToName ?? mail.To, mail.To));
        msg.Subject = mail.Subject;
        msg.Body = new BodyBuilder { HtmlBody = mail.Html, TextBody = HtmlToText(mail.Html) }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = s.Security switch
        {
            MailSecurity.StartTls => SecureSocketOptions.StartTls,
            MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            MailSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(s.Host, s.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(s.UserName)) await client.AuthenticateAsync(s.UserName, password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static string HtmlToText(string html) =>
        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p|/div|/tr|/h\d)\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "<[^>]+>", "")).Trim();
}

/// <summary>Drains the queue: sends each mail with the current settings and records the outcome.</summary>
public class MailSender(MailQueue queue, IServiceScopeFactory scopes, ILogger<MailSender> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var mail in queue.ReadAllAsync(stoppingToken))
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            var entry = new MailLog { To = mail.To, Subject = mail.Subject };
            try
            {
                var s = await db.MailSettings.FindAsync([1], stoppingToken);
                if (s is not { Enabled: true } || string.IsNullOrWhiteSpace(s.Host))
                {
                    entry.Status = MailStatus.Skipped;
                    entry.Error = "E-mail notifications are switched off.";
                }
                else
                {
                    var pw = s.PasswordEncrypted is null ? null : secrets.Unprotect(s.PasswordEncrypted);
                    // One retry for transient SMTP hiccups.
                    try { await Smtp.SendAsync(s, pw, mail, stoppingToken); }
                    catch (Exception first) when (first is not OperationCanceledException)
                    {
                        await Task.Delay(5000, stoppingToken);
                        await Smtp.SendAsync(s, pw, mail, stoppingToken);
                    }
                    entry.Status = MailStatus.Sent;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Mail to {To} failed", mail.To);
                entry.Status = MailStatus.Failed;
                entry.Error = ex.Message;
            }
            db.MailLogs.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}

/// <summary>Turns workflow events into e-mails to the right people.</summary>
public class Notifier(AppDbContext db, MailQueue queue)
{
    public async Task DepartmentActionAsync(BudgetVersion v, string brand, DeptAction action, AppUser actor, DepartmentStatus st)
    {
        var settings = await db.MailSettings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == 1);
        if (settings is not { Enabled: true }) return;

        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == v.CompanyId);
        var brandName = await db.Brands.Where(b => b.CompanyId == v.CompanyId && b.Code == brand).Select(b => b.Name).FirstOrDefaultAsync() ?? brand;
        var ownerIds = await db.UserDepartments.Where(d => d.CompanyId == v.CompanyId && d.BrandCode == brand).Select(d => d.UserId).ToListAsync();

        List<int> recipients;
        string subject, headline, intro;
        var cc = $"{brand} · {brandName}";
        var budget = $"{v.Name} · Rev {v.RevisionNo} (FY {v.FiscalYear}, {company.Name})";
        switch (action)
        {
            case DeptAction.Submitted:
                // The submitter's manager; without one, every administrator.
                recipients = st.ApproverId is int a ? [a]
                    : await db.Users.Where(u => u.Active && u.Role == UserRole.Admin).Select(u => u.Id).ToListAsync();
                subject = $"Budget for {brand} submitted for your approval";
                headline = "A budget is waiting for your approval";
                intro = $"{actor.DisplayName} submitted the budget for <b>{E(cc)}</b>.";
                break;
            case DeptAction.Approved:
                recipients = [.. ownerIds, .. st.SubmittedById is int s1 ? new[] { s1 } : []];
                subject = $"Budget for {brand} approved";
                headline = "Your budget was approved";
                intro = $"{actor.DisplayName} approved the budget for <b>{E(cc)}</b>. It now counts in the company budget.";
                break;
            case DeptAction.Rejected:
                recipients = [.. ownerIds, .. st.SubmittedById is int s2 ? new[] { s2 } : []];
                subject = $"Budget for {brand} needs changes";
                headline = "Your budget was sent back for changes";
                intro = $"{actor.DisplayName} rejected the budget for <b>{E(cc)}</b>. Make the changes and submit it again.";
                break;
            default:
                recipients = [.. ownerIds, .. st.SubmittedById is int s3 ? new[] { s3 } : []];
                subject = $"Budget for {brand} reopened";
                headline = "A budget was reopened for changes";
                intro = $"{actor.DisplayName} reopened the budget for <b>{E(cc)}</b>. It can be edited again and needs a new submission.";
                break;
        }
        var link = $"{settings.AppUrl.TrimEnd('/')}/#/budgets/{v.Id}?b={Uri.EscapeDataString(brand)}";
        await SendToUsersAsync(recipients.Where(id => id != actor.Id), subject,
            Template(headline, intro, budget, st.Comment, action == DeptAction.Submitted ? "Review the budget" : "Open the budget", link));
    }

    /// <summary>The whole revision was approved by finance — tell everyone who owns a department in it.</summary>
    public async Task BudgetApprovedAsync(BudgetVersion v, AppUser actor)
    {
        var settings = await db.MailSettings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == 1);
        if (settings is not { Enabled: true }) return;
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == v.CompanyId);
        var brands = await db.Lines.Where(l => l.VersionId == v.Id).Select(l => l.BrandCode).Distinct().ToListAsync();
        var owners = await db.UserDepartments.Where(d => d.CompanyId == v.CompanyId && brands.Contains(d.BrandCode)).Select(d => d.UserId).Distinct().ToListAsync();
        var link = $"{settings.AppUrl.TrimEnd('/')}/#/budgets/{v.Id}";
        await SendToUsersAsync(owners.Where(id => id != actor.Id), $"{v.Name} · Rev {v.RevisionNo} approved ({company.Name})",
            Template("The company budget is approved",
                $"{actor.DisplayName} approved <b>{E(v.Name)} · Rev {v.RevisionNo}</b> for {E(company.Name)}. It is now locked and will be loaded into SAP Business One.",
                $"FY {v.FiscalYear}, {company.Name}", null, "Open the budget", link));
    }

    private async Task SendToUsersAsync(IEnumerable<int> userIds, string subject, string html)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.Active).ToListAsync();
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Email))
            {
                // Make the gap visible in the mail log rather than silently dropping it.
                db.MailLogs.Add(new MailLog { To = u.DisplayName, Subject = subject, Status = MailStatus.Skipped, Error = "User has no e-mail address." });
                continue;
            }
            queue.Enqueue(new OutgoingMail(u.Email, u.DisplayName, subject, html));
        }
        await db.SaveChangesAsync();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Simple, e-mail-client-safe HTML (tables + inline styles) in the Nexus colours.</summary>
    public static string Template(string headline, string introHtml, string context, string? comment, string button, string link) => $"""
        <div style="background:#f5f6fb;padding:24px 12px;font-family:'Segoe UI',Arial,sans-serif;color:#1b1f2e">
          <table role="presentation" cellpadding="0" cellspacing="0" style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e3e6f0;border-radius:8px">
            <tr><td style="padding:18px 24px;border-bottom:1px solid #e3e6f0">
              <span style="display:inline-block;width:18px;height:18px;border-radius:5px;background:#3b54d4;vertical-align:middle"></span>
              <b style="font-size:15px;vertical-align:middle;margin-left:6px">Nexus</b>
              <span style="font-size:12px;color:#5a6480;vertical-align:middle">B1 Budget</span>
            </td></tr>
            <tr><td style="padding:22px 24px">
              <h2 style="margin:0 0 10px;font-size:18px">{E(headline)}</h2>
              <p style="margin:0 0 6px;font-size:14px;line-height:1.5">{introHtml}</p>
              <p style="margin:0 0 14px;font-size:13px;color:#5a6480">{E(context)}</p>
              {(string.IsNullOrWhiteSpace(comment) ? "" : $"""<p style="margin:0 0 16px;padding:10px 12px;background:#eef1fb;border-radius:5px;font-size:13px">“{E(comment)}”</p>""")}
              <a href="{E(link)}" style="display:inline-block;background:#3b54d4;color:#ffffff;text-decoration:none;padding:9px 16px;border-radius:5px;font-size:13px;font-weight:600">{E(button)}</a>
            </td></tr>
            <tr><td style="padding:12px 24px;border-top:1px solid #e3e6f0;font-size:11px;color:#5a6480">You receive this because you own or approve this budget in Nexus B1 Budget.</td></tr>
          </table>
        </div>
        """;
}
