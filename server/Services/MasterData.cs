using B1Budget.Api.Data;
using B1Budget.Api.Domain;
using B1Budget.Api.Services.B1;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Services;

public static class MasterData
{
    /// <summary>Replace a company's cached brands (distribution rules of its dimension) and P&L accounts from B1.</summary>
    public static async Task<(int Brands, int Accounts)> LoadAsync(AppDbContext db, Company company, IB1Gateway gateway, CancellationToken ct)
    {
        var brands = await gateway.GetBrandsAsync(company.Dimension, ct);
        var accounts = await gateway.GetAccountsAsync(ct);
        await db.Brands.Where(b => b.CompanyId == company.Id).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.CompanyId == company.Id).ExecuteDeleteAsync(ct);
        db.Brands.AddRange(brands.Select(b => new Brand { CompanyId = company.Id, Code = b.Code, Name = b.Name, Active = b.Active }));
        db.Accounts.AddRange(accounts.Select(a => new GlAccount { CompanyId = company.Id, Code = a.Code, Name = a.Name, Kind = a.Kind, DisplayCode = a.DisplayCode }));
        company.MasterDataSyncedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (brands.Count, accounts.Count);
    }
}
