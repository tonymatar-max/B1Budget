using B1Budget.Api.Domain;

namespace B1Budget.Api.Services.B1;

public record BrandDto(string Code, string Name, bool Active);
public record AccountDto(string Code, string Name, AccountKind Kind, string? DisplayCode);

/// <summary>JDT1 totals for one account × brand × posting date.</summary>
public record ActualRow(string Account, string Brand, DateTime Date, decimal Debit, decimal Credit);

public record JournalLineDto(int TransId, int LineId, DateTime Date, string Account, string Brand,
    decimal Debit, decimal Credit, string? Memo, string? Ref1, string? TransType);

public record UpsertResult(PushAction Action, string? Key);

/// <summary>Everything the app needs from SAP B1. Two implementations: live Service Layer and an in-memory mock.</summary>
public interface IB1Gateway : IDisposable
{
    Task<string> TestConnectionAsync(CancellationToken ct);
    Task<List<BrandDto>> GetBrandsAsync(int dimension, CancellationToken ct);
    Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct);
    Task<List<ActualRow>> GetActualsAsync(int dimension, DateTime from, DateTime to, CancellationToken ct);
    Task<List<JournalLineDto>> GetJournalLinesAsync(int dimension, string account, string brand, DateTime from, DateTime to, CancellationToken ct);

    Task<BudgetFieldMap> GetBudgetFieldMapAsync(CancellationToken ct);
    /// <summary>Find the budget scenario for this name + fiscal year without creating it.</summary>
    Task<int?> FindScenarioAsync(string name, DateTime fiscalYearStart, CancellationToken ct);
    /// <summary>Find or create the budget scenario for this name + fiscal year; returns its numerator.</summary>
    Task<(int Numerator, bool Created)> EnsureScenarioAsync(string name, DateTime fiscalYearStart, CancellationToken ct);
    /// <summary>Create or update the native budget for one account in one scenario.</summary>
    Task<UpsertResult> UpsertBudgetAsync(int scenario, string account, AccountKind kind, decimal[] periods, CancellationToken ct);
    /// <summary>Zero an existing budget (no-op if B1 has none). Returns true if anything changed.</summary>
    Task<bool> ClearBudgetAsync(int scenario, string account, AccountKind kind, CancellationToken ct);
    /// <summary>Read back what B1 holds for one scenario (account → 12 periods), for reconciliation.</summary>
    Task<Dictionary<string, decimal[]>> ReadScenarioBudgetsAsync(int scenario, CancellationToken ct);
}
