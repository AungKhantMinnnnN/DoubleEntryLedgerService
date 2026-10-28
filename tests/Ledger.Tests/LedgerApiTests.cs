using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledger.Tests;

public class LedgerApiTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    readonly HttpClient http = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<LedgerDbContext>>();
            s.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(pg.ConnectionString));
        });
    }).CreateClient();

    static string Uniq() => Guid.NewGuid().ToString("N");

    async Task<Guid> NewAccount(string type, string ccy = "USD")
    {
        var res = await http.PostAsJsonAsync("/accounts", new { code = Uniq(), type, currency = ccy });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    Task<HttpResponseMessage> Post(Guid debit, Guid credit, decimal debitAmt, decimal creditAmt, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/journal-entries")
        {
            Content = JsonContent.Create(new
            {
                description = "t",
                lines = new[]
                {
                    new { accountId = debit, direction = "Debit", amount = debitAmt },
                    new { accountId = credit, direction = "Credit", amount = creditAmt },
                },
            }),
        };
        req.Headers.Add("Idempotency-Key", key ?? Uniq());
        return http.SendAsync(req);
    }

    async Task<decimal> Balance(Guid id) =>
        (await http.GetFromJsonAsync<JsonElement>($"/accounts/{id}/balance")).GetProperty("balance").GetDecimal();

    [Fact]
    public async Task Posting_updates_balances_by_normal_side()
    {
        var cash = await NewAccount("Asset");
        var revenue = await NewAccount("Revenue");
        Assert.Equal(HttpStatusCode.Created, (await Post(cash, revenue, 100.5m, 100.5m)).StatusCode);
        Assert.Equal(100.5m, await Balance(cash));
        Assert.Equal(100.5m, await Balance(revenue));
    }

    [Fact]
    public async Task Unbalanced_entry_is_422_and_changes_nothing()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(a, b, 10m, 9m)).StatusCode);
        Assert.Equal(0m, await Balance(a));
    }

    [Fact]
    public async Task Amount_with_too_many_decimals_is_rejected()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(a, b, 1.00001m, 1.00001m)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_account_code_is_409()
    {
        var body = new { code = Uniq(), type = "Asset", currency = "USD" };
        await http.PostAsJsonAsync("/accounts", body);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/accounts", body)).StatusCode);
    }

    [Fact]
    public async Task Transactions_paginate_newest_first_without_gaps()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        for (var i = 1; i <= 5; i++)
        {
            var r = await Post(a, b, i, i);
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        }

        var seen = new List<decimal>();
        string? cursor = null;
        do
        {
            var page = await http.GetFromJsonAsync<JsonElement>($"/accounts/{a}/transactions?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("amount").GetDecimal()));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);

        Assert.Equal([5m, 4m, 3m, 2m, 1m], seen);
    }

    [Fact]
    public async Task Replay_returns_original_response_and_posts_once()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        var key = Uniq();
        var first = await Post(a, b, 10m, 10m, key);
        var second = await Post(a, b, 10m, 10m, key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(10m, await Balance(a));
    }

    [Fact]
    public async Task Same_key_with_different_body_is_422()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        var key = Uniq();
        await Post(a, b, 10m, 10m, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(a, b, 11m, 11m, key)).StatusCode);
        Assert.Equal(10m, await Balance(a));
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_produce_exactly_one_entry()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        var key = Uniq();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Post(a, b, 10m, 10m, key)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(bodies.Distinct());
        Assert.Equal(10m, await Balance(a));

        await using var c = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var n = await new Npgsql.NpgsqlCommand($"""SELECT count(*) FROM "JournalEntries" WHERE "IdempotencyKey"='{key}'""", c).ExecuteScalarAsync();
        Assert.Equal(1L, n);
    }

    // Invariant: whatever the outcome mix, the balance equals the sum of the postings that succeeded.
    [Fact]
    public async Task Hot_account_contention_loses_no_updates()
    {
        var hot = await NewAccount("Asset"); var other = await NewAccount("Revenue");
        var responses = await Task.WhenAll(Enumerable.Range(1, 100).Select(i => Post(hot, other, i, i)));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(ok >= 90, $"only {ok}/100 succeeded"); // retries should absorb nearly all of it
        var expected = 0m;
        for (var i = 0; i < responses.Length; i++)
            if (responses[i].StatusCode == HttpStatusCode.Created) expected += i + 1;
        Assert.Equal(expected, await Balance(hot));
        Assert.Equal(expected, await Balance(other));
    }

    [Fact]
    public async Task Opposite_direction_postings_between_two_accounts_do_not_deadlock_forever()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Asset");
        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? Post(a, b, 1m, 1m) : Post(b, a, 1m, 1m)));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        // every posting moves 1 from one to the other, so the two balances always sum to zero
        Assert.Equal(0m, await Balance(a) + await Balance(b));
    }

    // ---- FX ----

    // Random currency codes keep these tests independent of each other (rates and clearing accounts are shared state).
    static string Ccy() => new(Enumerable.Range(0, 3).Select(_ => (char)('A' + Random.Shared.Next(26))).ToArray());

    Task<HttpResponseMessage> Rate(string b, string q, decimal rate, DateTimeOffset? at = null) =>
        http.PostAsJsonAsync("/fx-rates", new { @base = b, quote = q, rate, effectiveAt = at, source = "test" });

    Task<HttpResponseMessage> PostFx(Guid debit, string debitCcy, decimal amt, Guid credit)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/journal-entries")
        {
            Content = JsonContent.Create(new
            {
                lines = new object[]
                {
                    new { accountId = debit, direction = "Debit", amount = amt, currency = debitCcy },
                    new { accountId = credit, direction = "Credit", amount = amt, currency = debitCcy },
                },
            }),
        };
        req.Headers.Add("Idempotency-Key", Uniq());
        return http.SendAsync(req);
    }

    async Task<decimal> DbBalanceByCode(string code) => await DbBalanceByCodeOrNull(code) ?? throw new Xunit.Sdk.XunitException($"no clearing account for {code}");

    async Task<decimal?> DbBalanceByCodeOrNull(string code)
    {
        await using var c = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await c.OpenAsync();
        var v = await new Npgsql.NpgsqlCommand($"""SELECT b."Balance" FROM "Accounts" a JOIN "AccountBalances" b ON b."AccountId"=a."Id" WHERE a."Code"='FX-CLEARING-{code}'""", c).ExecuteScalarAsync();
        return v as decimal?;
    }

    [Fact]
    public async Task Cross_currency_entry_converts_and_balances_through_clearing()
    {
        string usd = Ccy(), eur = Ccy();
        Assert.Equal(HttpStatusCode.Created, (await Rate(usd, eur, 0.92m)).StatusCode);
        var cash = await NewAccount("Asset", usd);       // debit 100 usd
        var revenue = await NewAccount("Revenue", eur);  // credit 100 usd => 92 eur

        var res = await PostFx(cash, usd, 100m, revenue);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode); // commit succeeded => the DB trigger was satisfied per currency

        Assert.Equal(100m, await Balance(cash));
        Assert.Equal(92m, await Balance(revenue));
        // Booked: usd +100 debit / eur 92 credit. Each currency is unbalanced on its own, so each clearing account
        // takes the opposite side: usd clearing is credited 100 (+100, equity), eur clearing debited 92 (-92).
        Assert.Equal(100m, await DbBalanceByCode(usd));
        Assert.Equal(-92m, await DbBalanceByCode(eur));
        var entry = await res.Content.ReadFromJsonAsync<JsonElement>();
        var line = entry.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountId").GetGuid() == revenue);
        Assert.Equal(92m, line.GetProperty("amount").GetDecimal());
        Assert.Equal(100m, line.GetProperty("originalAmount").GetDecimal());
        Assert.Equal(usd, line.GetProperty("originalCurrency").GetString());
        Assert.Equal(0.92m, line.GetProperty("fxRate").GetDecimal());
    }

    [Fact]
    public async Task Entry_converting_every_line_to_one_currency_needs_no_clearing()
    {
        string usd = Ccy(), eur = Ccy();
        await Rate(usd, eur, 0.92m);
        var a = await NewAccount("Asset", eur); var b = await NewAccount("Revenue", eur);
        // both lines are usd-denominated but booked in eur: each converts to 92, so eur balances itself
        // and no clearing account is created for it
        Assert.Equal(HttpStatusCode.Created, (await PostFx(a, usd, 100m, b)).StatusCode);
        Assert.Equal(92m, await Balance(a));
        Assert.Equal(92m, await Balance(b));
        Assert.Null(await DbBalanceByCodeOrNull(eur));
    }

    [Fact]
    public async Task Rate_change_does_not_alter_old_entries()
    {
        string usd = Ccy(), eur = Ccy();
        await Rate(usd, eur, 0.90m, DateTimeOffset.UtcNow.AddMinutes(-10));
        var cash = await NewAccount("Asset", usd); var revenue = await NewAccount("Revenue", eur);
        var created = await PostFx(cash, usd, 100m, revenue);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var before = await http.GetStringAsync($"/journal-entries/{id}");

        await Rate(usd, eur, 0.50m);

        Assert.Equal(before, await http.GetStringAsync($"/journal-entries/{id}"));
        Assert.Equal(90m, await Balance(revenue));
        // a new posting uses the new rate
        await PostFx(cash, usd, 100m, revenue);
        Assert.Equal(140m, await Balance(revenue));
    }

    [Fact]
    public async Task Inverse_rate_is_used_when_only_the_opposite_pair_is_quoted()
    {
        string usd = Ccy(), eur = Ccy();
        await Rate(eur, usd, 2m); // 1 eur = 2 usd  =>  100 usd = 50 eur
        var cash = await NewAccount("Asset", usd); var revenue = await NewAccount("Revenue", eur);
        Assert.Equal(HttpStatusCode.Created, (await PostFx(cash, usd, 100m, revenue)).StatusCode);
        Assert.Equal(50m, await Balance(revenue));
    }

    [Fact]
    public async Task Missing_rate_is_422()
    {
        string usd = Ccy(), eur = Ccy();
        var cash = await NewAccount("Asset", usd); var revenue = await NewAccount("Revenue", eur);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostFx(cash, usd, 100m, revenue)).StatusCode);
        Assert.Equal(0m, await Balance(cash));
    }

    [Fact]
    public async Task Fx_rate_validation()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rate("USD", "USD", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rate("USD", "EUR", 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rate("USD", "EUR", 1.123456789m)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Rate("usd", "EUR", 1m)).StatusCode);
    }

    // ---- Reversals ----

    Task<HttpResponseMessage> Reverse(Guid id, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/journal-entries/{id}/reverse");
        req.Headers.Add("Idempotency-Key", key ?? Uniq());
        return http.SendAsync(req);
    }

    static async Task<Guid> IdOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Reversal_restores_balances_and_links_to_the_original()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 25m, 25m));

        var res = await Reverse(id);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rev = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, rev.GetProperty("reversesEntryId").GetGuid());
        var cashLine = rev.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountId").GetGuid() == cash);
        Assert.Equal("Credit", cashLine.GetProperty("direction").GetString());
        Assert.Equal(0m, await Balance(cash));
        Assert.Equal(0m, await Balance(revenue));
    }

    [Fact]
    public async Task Original_entry_is_untouched_by_its_reversal()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 25m, 25m));
        var before = await http.GetStringAsync($"/journal-entries/{id}");
        await Reverse(id);
        Assert.Equal(before, await http.GetStringAsync($"/journal-entries/{id}"));
    }

    [Fact]
    public async Task Reversing_an_fx_entry_also_unwinds_clearing_and_uses_the_stored_rate()
    {
        string usd = Ccy(), eur = Ccy();
        await Rate(usd, eur, 0.92m, DateTimeOffset.UtcNow.AddMinutes(-10));
        var cash = await NewAccount("Asset", usd); var revenue = await NewAccount("Revenue", eur);
        var id = await IdOf(await PostFx(cash, usd, 100m, revenue));
        await Rate(usd, eur, 0.50m); // must not matter to the reversal

        Assert.Equal(HttpStatusCode.Created, (await Reverse(id)).StatusCode);
        Assert.Equal(0m, await Balance(cash));
        Assert.Equal(0m, await Balance(revenue));
        Assert.Equal(0m, await DbBalanceByCode(usd));
        Assert.Equal(0m, await DbBalanceByCode(eur));
    }

    [Fact]
    public async Task Entry_can_be_reversed_only_once()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 5m, 5m));
        Assert.Equal(HttpStatusCode.Created, (await Reverse(id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Reverse(id)).StatusCode);
        Assert.Equal(0m, await Balance(cash));
    }

    [Fact]
    public async Task Reverse_replay_with_same_key_returns_the_same_response()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 5m, 5m));
        var key = Uniq();
        var first = await Reverse(id, key); var second = await Reverse(id, key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_reversals_with_different_keys_reverse_exactly_once()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 5m, 5m));
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Reverse(id)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(0m, await Balance(cash));
    }

    [Fact]
    public async Task Reverse_unknown_entry_is_404_and_needs_a_key()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Reverse(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/journal-entries/{Guid.NewGuid()}/reverse", null)).StatusCode);
    }

    // ---- Outbox (written atomically with the entry) ----

    async Task<List<(string type, JsonElement payload)>> EventsFor(Guid entryId)
    {
        await using var db = pg.NewContext();
        var rows = await db.Outbox.Where(o => o.Key == entryId.ToString()).OrderBy(o => o.CreatedAt).ToListAsync();
        return rows.Select(r => (r.Type, JsonDocument.Parse(r.Payload).RootElement.Clone())).ToList();
    }

    [Fact]
    public async Task Posting_writes_one_EntryPosted_event_with_the_contract_fields()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 7m, 7m));

        var (type, e) = Assert.Single(await EventsFor(id));
        Assert.Equal("EntryPosted", type);
        Assert.Equal("EntryPosted", e.GetProperty("type").GetString());
        Assert.Equal(1, e.GetProperty("version").GetInt32());
        Assert.Equal(id, e.GetProperty("entryId").GetGuid());
        Assert.NotEqual(Guid.Empty, e.GetProperty("eventId").GetGuid());
        Assert.Equal(2, e.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Reversal_writes_an_EntryReversed_event()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var id = await IdOf(await Post(cash, revenue, 7m, 7m));
        var revId = await IdOf(await Reverse(id));

        var (type, e) = Assert.Single(await EventsFor(revId));
        Assert.Equal("EntryReversed", type);
        Assert.Equal(id, e.GetProperty("reversesEntryId").GetGuid());
    }

    [Fact]
    public async Task Rejected_and_replayed_requests_write_no_extra_events()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        await Post(cash, revenue, 10m, 9m); // 422, nothing stored
        var key = Uniq();
        var id = await IdOf(await Post(cash, revenue, 3m, 3m, key));
        await Post(cash, revenue, 3m, 3m, key); // replay

        Assert.Single(await EventsFor(id));
        await using var db = pg.NewContext();
        Assert.Equal(0, await db.Outbox.CountAsync(o => o.Payload.Contains(cash.ToString()) && o.Key != id.ToString()));
    }

    [Fact]
    public async Task Concurrent_duplicates_write_exactly_one_event()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        var key = Uniq();
        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Post(cash, revenue, 4m, 4m, key)));
        var id = await IdOf(responses[0]);
        Assert.Single(await EventsFor(id));
    }

    // ---- Audit trail (written atomically with the change) ----

    async Task<List<string>> AuditActions(Guid id)
    {
        await using var db = pg.NewContext();
        return await db.AuditLog.Where(a => a.Payload.Contains(id.ToString())).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public async Task Account_posting_and_reversal_are_audited()
    {
        var cash = await NewAccount("Asset"); var revenue = await NewAccount("Revenue");
        Assert.Equal(["account.created"], await AuditActions(cash));

        var id = await IdOf(await Post(cash, revenue, 7m, 7m));
        var revId = await IdOf(await Reverse(id));
        // the reversal's row mentions the original id too (reversesEntryId), so both show up under it
        Assert.Equal(["entry.posted", "entry.reversed"], await AuditActions(id));
        Assert.Equal(["entry.reversed"], await AuditActions(revId));
    }

    [Fact]
    public async Task Fx_rates_and_clearing_accounts_are_audited()
    {
        string usd = Ccy(), eur = Ccy();
        var rate = await (await Rate(usd, eur, 0.9m)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["fx_rate.recorded"], await AuditActions(rate.GetProperty("id").GetGuid()));

        var cash = await NewAccount("Asset", usd); var revenue = await NewAccount("Revenue", eur);
        await PostFx(cash, usd, 10m, revenue);
        await using var db = pg.NewContext();
        Assert.Equal(2, await db.AuditLog.CountAsync(a => a.Action == "account.created" && a.Payload.Contains("\"system\":true")
            && (a.Payload.Contains($"FX-CLEARING-{usd}") || a.Payload.Contains($"FX-CLEARING-{eur}"))));
    }

    [Fact]
    public async Task Failed_requests_leave_no_audit_rows()
    {
        var code = Uniq();
        await http.PostAsJsonAsync("/accounts", new { code, type = "Asset", currency = "USD" });
        await http.PostAsJsonAsync("/accounts", new { code, type = "Asset", currency = "USD" }); // 409, rolled back

        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        await Post(a, b, 10m, 9m);                       // 422
        var key = Uniq();
        var id = await IdOf(await Post(a, b, 3m, 3m, key));
        await Post(a, b, 3m, 3m, key);                   // replay

        await using var db = pg.NewContext();
        Assert.Equal(1, await db.AuditLog.CountAsync(x => x.Action == "account.created" && x.Payload.Contains(code)));
        Assert.Equal(1, await db.AuditLog.CountAsync(x => x.Action == "entry.posted" && x.Payload.Contains(key)));
        Assert.Equal(["account.created", "entry.posted"], (await AuditActions(a)).Distinct().ToList());
    }

    [Fact]
    public async Task Concurrent_duplicates_write_exactly_one_audit_row()
    {
        var a = await NewAccount("Asset"); var b = await NewAccount("Revenue");
        var key = Uniq();
        await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Post(a, b, 4m, 4m, key)));
        await using var db = pg.NewContext();
        Assert.Equal(1, await db.AuditLog.CountAsync(x => x.Action == "entry.posted" && x.Payload.Contains(key)));
    }
}
