using Microsoft.AspNetCore.Hosting;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Tests;

public class ApiTests(WebApplicationFactory<Program> f) : IClassFixture<WebApplicationFactory<Program>>
{
    // Testing env skips the dev-only auto-migrate (no DB needed here).
    HttpClient Client() => f.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();

    [Fact]
    public async Task Health_is_ok() =>
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync("/health")).StatusCode);

    [Fact]
    public async Task Posting_without_idempotency_key_is_rejected() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsync("/journal-entries", null)).StatusCode);
}
