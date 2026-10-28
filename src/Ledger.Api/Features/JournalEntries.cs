using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledger.Api.Data;
using Ledger.Api.Domain;
using Ledger.Api.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Api.Features;

// Currency is the currency Amount is expressed in; defaults to the account currency. If it differs, the line is converted.
public record LineRequest(Guid AccountId, Direction Direction, decimal Amount, string? Currency = null);

record Planned(Guid AccountId, Direction Direction, decimal Amount, string Currency, decimal? OriginalAmount, string? OriginalCurrency, decimal? FxRate);
public record PostEntryRequest(string? Description, string? Reference, List<LineRequest>? Lines);

public record EntryLineDto(Guid Id, Guid AccountId, Direction Direction, decimal Amount, string Currency,
    decimal? OriginalAmount, string? OriginalCurrency, decimal? FxRate);
public record EntryDto(Guid Id, DateTimeOffset PostedAt, string? Description, string? Reference, Guid? ReversesEntryId, List<EntryLineDto> Lines);
public record ReverseRequest(string? Description, string? Reference);

public static class JournalEntries
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Produces/ProducesProblem only feed the dev-time OpenAPI document; they don't change behavior.
        app.MapPost("/journal-entries", Post)
            .Produces<EntryDto>(201).ProducesProblem(400).ProducesProblem(409).ProducesProblem(422);
        app.MapPost("/journal-entries/{id:guid}/reverse", Reverse)
            .Produces<EntryDto>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        app.MapGet("/journal-entries/{id:guid}", async (Guid id, LedgerDbContext db) =>
            await db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id) is { } e
                ? Results.Ok(ToDto(e)) : Results.NotFound())
            .Produces<EntryDto>().ProducesProblem(404);
    }

    // A reversal mirrors the booked lines exactly (same accounts, amounts, FX data and clearing lines) with the
    // direction flipped, so it needs no rate lookup and always nets the original to zero.
    static async Task<IResult> Reverse(Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, LedgerDbContext db, ILogger<Program> log, ReverseRequest? r = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return Results.Problem("Idempotency-Key header required", statusCode: 400);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, r?.Description, r?.Reference }))));
        if (await Replay(key, hash, db) is { } replay) return replay;

        var original = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id);
        if (original is null) return Results.NotFound();
        if (await db.JournalEntries.AnyAsync(e => e.ReversesEntryId == id)) return Results.Problem("entry is already reversed", statusCode: 409);

        var planned = original.Lines.Select(l => new Planned(l.AccountId, l.Direction == Direction.Debit ? Direction.Credit : Direction.Debit,
            l.Amount, l.Currency, l.OriginalAmount, l.OriginalCurrency, l.FxRate)).ToList();
        var accountIds = planned.Select(p => p.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        return await Persist(key, hash, r?.Description ?? $"Reversal of {id}", r?.Reference ?? original.Reference, id, planned, accounts, db, log);
    }

    static async Task<IResult> Post([FromHeader(Name = "Idempotency-Key")] string? key, PostEntryRequest r, LedgerDbContext db, ILogger<Program> log)
    {
        if (string.IsNullOrWhiteSpace(key)) return Results.Problem("Idempotency-Key header required", statusCode: 400);
        // Same key + same body replays the original response; same key + different body is a client bug.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r))));
        if (await Replay(key, hash, db) is { } replay) return replay;

        if (r.Lines is not { Count: >= 2 } lines) return Problem("an entry needs at least 2 lines");
        if (lines.Any(l => l.Amount <= 0 || !Money.FitsScale(l.Amount) || !Enum.IsDefined(l.Direction)))
            return Problem($"amounts must be > 0 with at most {Money.Scale} decimal places");

        var lineAccountIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => lineAccountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        if (lines.Any(l => !accounts.ContainsKey(l.AccountId))) return Problem("unknown account in lines");
        if (accounts.Values.Any(a => a.Status != AccountStatus.Active)) return Problem("account is not active");

        // The client balances the entry in the currency each line is expressed in.
        string LineCcy(LineRequest l) => l.Currency ?? accounts[l.AccountId].Currency;
        if (lines.Any(l => l.Currency is not null && !(l.Currency.Length == 3 && l.Currency.All(char.IsAsciiLetterUpper))))
            return Problem("line currency must be a 3-letter uppercase code");
        if (lines.GroupBy(LineCcy).Any(g => g.Sum(l => l.Direction == Direction.Debit ? l.Amount : -l.Amount) != 0))
            return Problem("debits must equal credits in each currency");

        // Convert lines whose currency differs from their account's, using the rate in force now.
        // The rate is resolved once and stored on the line, so history stays reproducible and retries are stable.
        var rates = new Dictionary<(string, string), decimal?>();
        var planned = new List<Planned>();
        foreach (var l in lines)
        {
            var acct = accounts[l.AccountId];
            var from = LineCcy(l);
            if (from == acct.Currency) { planned.Add(new(l.AccountId, l.Direction, l.Amount, acct.Currency, null, null, null)); continue; }

            if (!rates.TryGetValue((from, acct.Currency), out var rate))
                rates[(from, acct.Currency)] = rate = await Fx.Resolve(db, from, acct.Currency);
            if (rate is null) return Problem($"no FX rate for {from}->{acct.Currency}");
            var converted = Money.Round(l.Amount * rate.Value);
            if (converted <= 0) return Problem("converted amount rounds to zero");
            planned.Add(new(l.AccountId, l.Direction, converted, acct.Currency, l.Amount, from, rate));
        }

        // Conversion leaves each booked currency unbalanced; plug it with that currency's FX clearing account
        // so the database invariant (debits = credits per currency) still holds.
        foreach (var g in planned.GroupBy(p => p.Currency).ToList())
        {
            var net = g.Sum(p => p.Direction == Direction.Debit ? p.Amount : -p.Amount);
            if (net == 0) continue;
            var clearing = await Fx.Clearing(db, g.Key);
            accounts[clearing.Id] = clearing;
            planned.Add(new(clearing.Id, net > 0 ? Direction.Credit : Direction.Debit, Math.Abs(net), g.Key, null, null, null));
        }
        return await Persist(key, hash, r.Description, r.Reference, null, planned, accounts, db, log);
    }

    static async Task<IResult> Persist(string key, string hash, string? description, string? reference, Guid? reverses,
        List<Planned> planned, Dictionary<Guid, Account> accounts, LedgerDbContext db, ILogger log)
    {
        var ids = planned.Select(p => p.AccountId).Distinct().ToList();

        // Optimistic concurrency: the balance rows carry an xmin token, so a concurrent writer makes our
        // UPDATE fail instead of overwriting. Each retry re-reads the balances and redoes the whole write.
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var postedAt = DateTimeOffset.UtcNow; // copied onto the lines so account history can be served from one index
            var entry = new JournalEntry
            {
                IdempotencyKey = key,
                PostedAt = postedAt,
                RequestHash = hash, // hash of the re-serialized body, so whitespace/property order don't matter
                Description = description,
                Reference = reference,
                ReversesEntryId = reverses,
                Lines = planned.Select(p => new JournalLine
                {
                    AccountId = p.AccountId, Direction = p.Direction, Amount = p.Amount, Currency = p.Currency, PostedAt = postedAt,
                    OriginalAmount = p.OriginalAmount, OriginalCurrency = p.OriginalCurrency, FxRate = p.FxRate,
                }).ToList(),
            };
            db.JournalEntries.Add(entry);
            // Same SaveChanges, same transaction: the event exists if and only if the entry does.
            db.Outbox.Add(OutboxEvents.Create(entry, reverses is null ? OutboxEvents.Posted : OutboxEvents.Reversed));
            Audit.Record(db, reverses is null ? "entry.posted" : "entry.reversed",
                new { entryId = entry.Id, idempotencyKey = key, reversesEntryId = reverses, accountIds = ids });

            var balances = await db.AccountBalances.Where(b => ids.Contains(b.AccountId)).ToDictionaryAsync(b => b.AccountId);
            foreach (var l in entry.Lines)
                balances[l.AccountId].Balance += Money.Signed(accounts[l.AccountId].Type, l.Direction, l.Amount);

            try
            {
                await db.SaveChangesAsync();
                return Created(entry);
            }
            catch (Exception e) when (IsRetryable(e) || IsDuplicateKey(e))
            {
                db.ChangeTracker.Clear();
                // A concurrent request with our key may have won; replay its result.
                if (await Replay(key, hash, db) is { } won) return won;
                if (IsDuplicateKey(e))
                    return Results.Problem(reverses is null ? "conflicting concurrent request" : "entry is already reversed", statusCode: 409);
                log.LogDebug("posting conflict on attempt {Attempt}, retrying", attempt + 1);
                await Task.Delay(Random.Shared.Next(0, Math.Min(250, 5 << Math.Min(attempt, 6))) + 1); // full jitter, capped
            }
        }
        log.LogWarning("gave up posting entry after {Attempts} attempts (contention on accounts {AccountIds})", MaxAttempts, ids);
        return Results.Problem("too much contention, retry", statusCode: 409);
    }

    // ponytail: fixed attempts, no per-account queueing; raise MaxAttempts or serialize hot accounts if the load test shows 409s.
    const int MaxAttempts = 25;

    static bool IsDuplicateKey(Exception e) => e.InnerException is PostgresException { SqlState: "23505" };

    // xmin conflict, serialization failure, or deadlock (rows locked in opposite order by two postings).
    static bool IsRetryable(Exception e) =>
        e is DbUpdateConcurrencyException || e.InnerException is PostgresException { SqlState: "40001" or "40P01" };

    // One shape for create, replay and GET, so a replay is byte-identical: fixed money scale,
    // postgres timestamp precision (µs), stable line order.
    static IResult Created(JournalEntry e) => Results.Created($"/journal-entries/{e.Id}", ToDto(e));

    internal static EntryDto ToDto(JournalEntry e) => new(e.Id, new DateTimeOffset(e.PostedAt.UtcTicks / 10 * 10, TimeSpan.Zero),
        e.Description, e.Reference, e.ReversesEntryId,
        e.Lines.OrderBy(l => l.Id).Select(l => new EntryLineDto(l.Id, l.AccountId, l.Direction, l.Amount + 0.0000m, l.Currency,
            l.OriginalAmount + 0.0000m, l.OriginalCurrency, l.FxRate + 0.00000000m)).ToList());

    static async Task<IResult?> Replay(string key, string hash, LedgerDbContext db)
    {
        var existing = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).FirstOrDefaultAsync(e => e.IdempotencyKey == key);
        if (existing is null) return null;
        return existing.RequestHash == hash
            ? Created(existing)
            : Problem("Idempotency-Key was already used with a different request body");
    }

    static IResult Problem(string detail) => Results.Problem(detail, statusCode: 422);
}
