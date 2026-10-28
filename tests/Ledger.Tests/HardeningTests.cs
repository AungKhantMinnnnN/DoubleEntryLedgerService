using System.Net;
using System.Net.Http.Json;
using Ledger.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Ledger.Tests;

public class HardeningTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    HttpClient Client(string? connectionString = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<LedgerDbContext>>();
            s.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(connectionString ?? pg.ConnectionString));
        });
    }).CreateClient();

    // Nothing listens on this port, so every DB call fails fast.
    string Dead => new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Port = 1, Timeout = 2 }.ConnectionString;

    static void AssertProblem(HttpResponseMessage r, HttpStatusCode status)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_route_is_problem_json() => AssertProblem(await Client().GetAsync("/nope"), HttpStatusCode.NotFound);

    [Fact]
    public async Task Malformed_json_is_400_problem_json() =>
        AssertProblem(await Client().PostAsync("/accounts", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);

    [Fact]
    public async Task Domain_errors_use_the_same_shape()
    {
        var http = Client();
        AssertProblem(await http.PostAsync("/journal-entries", null), HttpStatusCode.BadRequest);
        var code = Guid.NewGuid().ToString("N");
        await http.PostAsJsonAsync("/accounts", new { code, type = "Asset", currency = "USD" });
        AssertProblem(await http.PostAsJsonAsync("/accounts", new { code, type = "Asset", currency = "USD" }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unhandled_exception_is_500_problem_json_without_leaking_internals()
    {
        var res = await Client(Dead).PostAsJsonAsync("/accounts", new { code = "x", type = "Asset", currency = "USD" });
        AssertProblem(res, HttpStatusCode.InternalServerError);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Host=", body);
        Assert.DoesNotContain("Npgsql", body);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_generated_and_present_on_errors()
    {
        var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Correlation-Id", "abc-123");
        Assert.Equal("abc-123", (await http.SendAsync(req)).Headers.GetValues("X-Correlation-Id").Single());

        Assert.NotEmpty((await http.GetAsync("/health")).Headers.GetValues("X-Correlation-Id").Single());
        Assert.NotEmpty((await Client(Dead).PostAsJsonAsync("/accounts", new { code = "x", type = "Asset", currency = "USD" })).Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Readiness_reflects_the_database_while_liveness_does_not()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/health/ready")).StatusCode);

        var broken = Client(Dead);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await broken.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await broken.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Line_PostedAt_always_equals_its_entrys()
    {
        var http = Client();
        async Task<Guid> Acct(string type) => (await (await http.PostAsJsonAsync("/accounts", new { code = Guid.NewGuid().ToString("N"), type, currency = "USD" })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var (a, b) = (await Acct("Asset"), await Acct("Revenue"));
        var req = new HttpRequestMessage(HttpMethod.Post, "/journal-entries") { Content = JsonContent.Create(new { lines = new object[] {
            new { accountId = a, direction = "Debit", amount = 1m }, new { accountId = b, direction = "Credit", amount = 1m } } }) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await http.SendAsync(req);

        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var bad = await new NpgsqlCommand("""SELECT count(*) FROM "JournalLines" l JOIN "JournalEntries" e ON e."Id"=l."EntryId" WHERE l."PostedAt" <> e."PostedAt" """, c).ExecuteScalarAsync();
        Assert.Equal(0L, bad);
    }

    // Tiny tables make the planner pick a seq or bitmap scan, so forbid those and check the history query *can* use the index.
    [Fact]
    public async Task History_query_can_be_served_by_the_account_postedat_index()
    {
        await using var c = new NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        await new NpgsqlCommand("SET enable_seqscan = off; SET enable_bitmapscan = off", c).ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand("""
            EXPLAIN SELECT * FROM "JournalLines" WHERE "AccountId" = '11111111-1111-1111-1111-111111111111'
            ORDER BY "PostedAt" DESC, "Id" DESC LIMIT 51
            """, c);
        var plan = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) plan.Add(r.GetString(0));
        var text = string.Join("\n", plan);
        Assert.Contains("IX_JournalLines_AccountId_PostedAt_Id", text);
        Assert.DoesNotContain("Sort", text); // ordered straight from the index
    }
}
