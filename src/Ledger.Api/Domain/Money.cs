namespace Ledger.Api.Domain;

public static class Money
{
    public const int Scale = 4; // matches numeric(19,4)

    // Round half to even (banker's rounding); used only where a computed amount is produced (FX, phase 5).
    public static decimal Round(decimal v) => Math.Round(v, Scale, MidpointRounding.ToEven);

    // Client-supplied amounts are never rounded silently; they must already fit the scale.
    public static bool FitsScale(decimal v) => v == Math.Round(v, Scale);

    // Asset/Expense grow with debits; the rest grow with credits.
    public static decimal Signed(AccountType t, Direction d, decimal amount) =>
        (t is AccountType.Asset or AccountType.Expense) == (d == Direction.Debit) ? amount : -amount;
}
