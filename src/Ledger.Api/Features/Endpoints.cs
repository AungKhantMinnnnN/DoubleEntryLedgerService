namespace Ledger.Api.Features;

public static class Endpoints
{
    public static void MapLedger(this IEndpointRouteBuilder app)
    {
        Accounts.Map(app);
        JournalEntries.Map(app);
        Fx.Map(app);
    }
}
