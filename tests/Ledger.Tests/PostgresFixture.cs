using Ledger.Api.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Ledger.Tests;

public class PostgresFixture : IAsyncLifetime
{
    readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:17").Build();

    public string ConnectionString => _pg.GetConnectionString();

    // The unprivileged role the service is meant to run as.
    public string AppConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString)
        { Username = "ledger_app", Password = "ledger_app" }.ConnectionString;

    public LedgerDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        // Same role db/init.sql creates; must exist before migrating so the migration grants it access.
        await using (var c = new Npgsql.NpgsqlConnection(ConnectionString))
        {
            await c.OpenAsync();
            await new Npgsql.NpgsqlCommand("CREATE ROLE ledger_app LOGIN PASSWORD 'ledger_app'", c).ExecuteNonQueryAsync();
        }
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();
}
