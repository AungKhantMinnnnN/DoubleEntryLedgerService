namespace Ledger.Api.Domain;

public enum AccountType { Asset, Liability, Equity, Revenue, Expense }
public enum AccountStatus { Active, Closed }
public enum Direction { Debit, Credit }

public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public AccountType Type { get; set; }
    public required string Currency { get; set; }
    public AccountStatus Status { get; set; }
}

// Immutable once posted. Corrections are reversing entries.
public class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public DateTimeOffset PostedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public Guid? ReversesEntryId { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public class JournalLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntryId { get; set; }
    public Guid AccountId { get; set; }
    public Direction Direction { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTimeOffset PostedAt { get; set; } // denormalized from the entry (immutable) for the history index
    // FX lines only: Round(OriginalAmount * FxRate) = Amount, with Amount in the account currency
    public decimal? OriginalAmount { get; set; }
    public string? OriginalCurrency { get; set; }
    public decimal? FxRate { get; set; }
}

public class AccountBalance
{
    public Guid AccountId { get; set; }
    public decimal Balance { get; set; }
    public uint Version { get; set; } // maps to xmin
}

public class FxRate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Base { get; set; }
    public required string Quote { get; set; }
    public decimal Rate { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public required string Source { get; set; }
}

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string Type { get; set; } // EntryPosted | EntryReversed
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

public class AuditLog // append-only, enforced by DB triggers
{
    public long Id { get; set; }
    public required string Action { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
