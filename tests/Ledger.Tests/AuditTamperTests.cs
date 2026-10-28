using Npgsql;

namespace Ledger.Tests;

// "Tamper-attempt demo": every way of rewriting audit history that the database is supposed to refuse.
public class AuditTamperTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    async Task<NpgsqlConnection> Open(string cs)
    {
        var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        return c;
    }

    static Task<int> Run(NpgsqlConnection c, string sql) => new NpgsqlCommand(sql, c).ExecuteNonQueryAsync();

    async Task SeedRow()
    {
        await using var c = await Open(pg.ConnectionString);
        await Run(c, """INSERT INTO "AuditLog" ("Action","Payload","At") VALUES ('seed','{}',now())""");
    }

    [Theory]
    [InlineData("""UPDATE "AuditLog" SET "Action"='tampered'""")]
    [InlineData("""DELETE FROM "AuditLog" """)]
    [InlineData("""TRUNCATE "AuditLog" """)]
    public async Task Owner_cannot_change_history_triggers_block_it(string sql)
    {
        await SeedRow();
        await using var c = await Open(pg.ConnectionString);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Run(c, sql));
        Assert.Contains("append-only", ex.MessageText);
    }

    [Theory]
    [InlineData("""UPDATE "AuditLog" SET "Action"='tampered'""")]
    [InlineData("""DELETE FROM "AuditLog" """)]
    [InlineData("""TRUNCATE "AuditLog" """)]
    public async Task App_role_is_denied_before_triggers_even_run(string sql)
    {
        await SeedRow();
        await using var c = await Open(pg.AppConnectionString);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Run(c, sql));
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    [Fact]
    public async Task App_role_can_still_append_and_do_its_normal_work()
    {
        await using var c = await Open(pg.AppConnectionString);
        Assert.Equal(1, await Run(c, """INSERT INTO "AuditLog" ("Action","Payload","At") VALUES ('ok','{}',now())"""));
        Assert.Equal(0, await Run(c, """UPDATE "AccountBalances" SET "Balance"="Balance" WHERE false"""));
    }
}
