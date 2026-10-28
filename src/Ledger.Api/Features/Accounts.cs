using Ledger.Api.Data;
using Ledger.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Api.Features;

public record CreateAccountRequest(string? Code, AccountType Type, string? Currency);

public static class Accounts
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Produces/ProducesProblem only feed the dev-time OpenAPI document; they don't change behavior.
        app.MapPost("/accounts", Create)
            .Produces<Account>(201).ProducesProblem(409).ProducesProblem(422);
        app.MapGet("/accounts/{id:guid}", async (Guid id, LedgerDbContext db) =>
            await db.Accounts.FindAsync(id) is { } a ? Results.Ok(a) : Results.NotFound())
            .Produces<Account>().ProducesProblem(404);
        app.MapGet("/accounts/{id:guid}/balance", Balance)
            .Produces(200).ProducesProblem(404);
        app.MapGet("/accounts/{id:guid}/transactions", Transactions)
            .Produces(200).ProducesProblem(404).ProducesProblem(422);
    }

    static async Task<IResult> Create(CreateAccountRequest r, LedgerDbContext db)
    {
        if (string.IsNullOrWhiteSpace(r.Code)) return Problem("code is required");
        if (r.Currency is not { Length: 3 } || !r.Currency.All(char.IsAsciiLetterUpper)) return Problem("currency must be a 3-letter uppercase code");
        if (!Enum.IsDefined(r.Type)) return Problem("invalid type");

        var a = new Account { Code = r.Code.Trim(), Type = r.Type, Currency = r.Currency, Status = AccountStatus.Active };
        db.Accounts.Add(a);
        Audit.Record(db, "account.created", new { accountId = a.Id, a.Code, a.Type, a.Currency });
        db.AccountBalances.Add(new AccountBalance { AccountId = a.Id });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        {
            return Results.Problem($"account code '{a.Code}' already exists", statusCode: 409);
        }
        return Results.Created($"/accounts/{a.Id}", a);
    }

    static async Task<IResult> Balance(Guid id, LedgerDbContext db)
    {
        var row = await db.Accounts.Where(a => a.Id == id)
            .Join(db.AccountBalances, a => a.Id, b => b.AccountId, (a, b) => new { a.Id, a.Currency, b.Balance })
            .FirstOrDefaultAsync();
        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // Keyset pagination on (PostedAt desc, LineId desc). cursor = "<PostedAt:O>|<LineId>" from the previous page.
    static async Task<IResult> Transactions(Guid id, LedgerDbContext db, int limit = 50, string? cursor = null)
    {
        limit = Math.Clamp(limit, 1, 200);
        if (!await db.Accounts.AnyAsync(a => a.Id == id)) return Results.NotFound();

        var q = db.JournalLines.Where(l => l.AccountId == id);

        if (cursor is not null)
        {
            var p = cursor.Split('|');
            if (p.Length != 2 || !DateTimeOffset.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) || !Guid.TryParse(p[1], out var lid))
                return Problem("invalid cursor");
            q = q.Where(l => l.PostedAt < at || (l.PostedAt == at && l.Id.CompareTo(lid) < 0));
        }

        // Page on the lines (index-only ordering), then join the headers for just those rows.
        var rows = await q.OrderByDescending(l => l.PostedAt).ThenByDescending(l => l.Id).Take(limit + 1)
            .Join(db.JournalEntries, l => l.EntryId, e => e.Id, (l, e) => new { l, e })
            .OrderByDescending(t => t.l.PostedAt).ThenByDescending(t => t.l.Id)
            .Select(t => new TransactionDto(t.l.Id, t.e.Id, t.l.PostedAt, t.l.Direction, t.l.Amount, t.l.Currency, t.e.Description, t.e.Reference))
            .ToListAsync();
        var next = rows.Count > limit ? $"{rows[limit - 1].PostedAt:O}|{rows[limit - 1].LineId}" : null;
        return Results.Ok(new { items = rows.Take(limit), nextCursor = next });
    }

    static IResult Problem(string detail) => Results.Problem(detail, statusCode: 422);
}

public record TransactionDto(Guid LineId, Guid EntryId, DateTimeOffset PostedAt, Direction Direction,
    decimal Amount, string Currency, string? Description, string? Reference);
