using Npgsql;

namespace Ledger.Tests;

public class BalanceTriggerTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    async Task<NpgsqlConnection> Open()
    {
        var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    async Task<Guid> SeedEntry()
    {
        var id = Guid.NewGuid();
        await using var c = await Open();
        await using var cmd = new NpgsqlCommand(
            """INSERT INTO "JournalEntries" ("Id","IdempotencyKey","RequestHash","PostedAt") VALUES (@id,@k,'h',now())""", c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("k", id.ToString());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    async Task PostLines(Guid entry, params (string dir, decimal amt, string ccy)[] lines)
    {
        await using var c = await Open();
        await using var tx = await c.BeginTransactionAsync();
        foreach (var (dir, amt, ccy) in lines)
        {
            await using var cmd = new NpgsqlCommand(
                """INSERT INTO "JournalLines" ("Id","EntryId","AccountId","Direction","Amount","Currency") VALUES (@id,@e,@a,@d,@amt,@c)""", c, tx);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("e", entry);
            cmd.Parameters.AddWithValue("a", Guid.NewGuid());
            cmd.Parameters.AddWithValue("d", dir);
            cmd.Parameters.AddWithValue("amt", amt);
            cmd.Parameters.AddWithValue("c", ccy);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync(); // deferred trigger fires here
    }

    [Fact]
    public async Task Balanced_entry_commits() =>
        await PostLines(await SeedEntry(), ("Debit", 10m, "USD"), ("Credit", 10m, "USD"));

    [Fact]
    public async Task Unbalanced_entry_fails_at_commit()
    {
        var e = await SeedEntry();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            PostLines(e, ("Debit", 10m, "USD"), ("Credit", 9.9999m, "USD")));
        Assert.Contains("unbalanced", ex.MessageText);
    }

    [Fact]
    public async Task Each_currency_must_balance_on_its_own()
    {
        var e = await SeedEntry();
        await Assert.ThrowsAsync<PostgresException>(() =>
            PostLines(e, ("Debit", 10m, "USD"), ("Credit", 10m, "EUR")));
    }

    [Fact]
    public async Task Audit_log_rejects_update_and_delete()
    {
        await using var c = await Open();
        await new NpgsqlCommand("""INSERT INTO "AuditLog" ("Action","Payload","At") VALUES ('x','{}',now())""", c).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("""UPDATE "AuditLog" SET "Action"='y'""", c).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("""DELETE FROM "AuditLog" """, c).ExecuteNonQueryAsync());
    }
}
