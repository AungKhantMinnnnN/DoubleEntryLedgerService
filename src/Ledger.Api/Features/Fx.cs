using Ledger.Api.Data;
using Ledger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Api.Features;

public record FxRateRequest(string? Base, string? Quote, decimal Rate, DateTimeOffset? EffectiveAt, string? Source);

public static class Fx
{
    const int RateScale = 8; // matches numeric(19,8)

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/fx-rates", Create)
        .Produces<FxRate>(201).ProducesProblem(422);

    static async Task<IResult> Create(FxRateRequest r, LedgerDbContext db)
    {
        if (!IsCode(r.Base) || !IsCode(r.Quote)) return Results.Problem("base and quote must be 3-letter uppercase codes", statusCode: 422);
        if (r.Base == r.Quote) return Results.Problem("base and quote must differ", statusCode: 422);
        if (r.Rate <= 0 || r.Rate != Math.Round(r.Rate, RateScale) || r.Rate >= 1e11m)
            return Results.Problem($"rate must be > 0 with at most {RateScale} decimal places", statusCode: 422);
        if (string.IsNullOrWhiteSpace(r.Source)) return Results.Problem("source is required", statusCode: 422);

        // Rates are append-only: a newer EffectiveAt supersedes an older one, nothing is overwritten.
        var rate = new FxRate { Base = r.Base!, Quote = r.Quote!, Rate = r.Rate, EffectiveAt = r.EffectiveAt ?? DateTimeOffset.UtcNow, Source = r.Source };
        db.FxRates.Add(rate);
        Audit.Record(db, "fx_rate.recorded", new { fxRateId = rate.Id, rate.Base, rate.Quote, rate.Rate, rate.EffectiveAt, rate.Source });
        await db.SaveChangesAsync();
        return Results.Created($"/fx-rates/{rate.Id}", rate);
    }

    static bool IsCode(string? c) => c is { Length: 3 } && c.All(char.IsAsciiLetterUpper);

    // Multiplier from -> to as of now. Falls back to the inverse of the opposite pair.
    public static async Task<decimal?> Resolve(LedgerDbContext db, string from, string to)
    {
        var now = DateTimeOffset.UtcNow;
        var direct = await db.FxRates.AsNoTracking().Where(x => x.Base == from && x.Quote == to && x.EffectiveAt <= now)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        var inverse = await db.FxRates.AsNoTracking().Where(x => x.Base == to && x.Quote == from && x.EffectiveAt <= now)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        // Whichever pair was quoted most recently wins.
        if (direct is not null && (inverse is null || direct.EffectiveAt >= inverse.EffectiveAt)) return direct.Rate;
        return inverse is null ? null : Math.Round(1m / inverse.Rate, RateScale, MidpointRounding.ToEven);
    }

    // Each currency gets one clearing account that absorbs the imbalance a conversion creates.
    public static async Task<Account> Clearing(LedgerDbContext db, string currency)
    {
        var code = $"FX-CLEARING-{currency}";
        var existing = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == code);
        if (existing is not null) return existing;

        var a = new Account { Code = code, Type = AccountType.Equity, Currency = currency, Status = AccountStatus.Active };
        db.Accounts.Add(a);
        Audit.Record(db, "account.created", new { accountId = a.Id, a.Code, a.Type, a.Currency, system = true });
        db.AccountBalances.Add(new AccountBalance { AccountId = a.Id });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear(); // another request created it first
            return await db.Accounts.AsNoTracking().FirstAsync(x => x.Code == code);
        }
        db.ChangeTracker.Clear();
        return a;
    }
}
