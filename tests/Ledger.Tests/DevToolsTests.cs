using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ledger.Tests;

// Swagger UI and the OpenAPI document exist only in Development. No database needed: nothing here touches it.
public class DevToolsTests
{
    static HttpClient Client(string environment) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment(environment);
        // Development config points at localhost Postgres/Kafka; blank those out so the app doesn't connect.
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Migrations"] = "",
            ["Kafka:BootstrapServers"] = "",
        }));
    }).CreateClient();

    [Fact]
    public async Task Development_serves_swagger_ui_and_the_openapi_document()
    {
        var http = Client("Development");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/swagger/index.html")).StatusCode);

        var doc = JsonDocument.Parse(await http.GetStringAsync("/openapi/v1.json")).RootElement;
        var paths = doc.GetProperty("paths");
        foreach (var route in new[] { "/accounts", "/accounts/{id}/balance", "/journal-entries", "/journal-entries/{id}/reverse", "/fx-rates" })
            Assert.True(paths.TryGetProperty(route, out _), $"{route} missing from the OpenAPI document");

        // The documented contract must match what the API does: 201 + problem responses, and the header is required.
        var post = paths.GetProperty("/journal-entries").GetProperty("post");
        var responses = post.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToList();
        Assert.Equal(["201", "400", "409", "422"], responses.Order().ToList());
        var header = post.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.True(header.GetProperty("required").GetBoolean());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Other_environments_expose_neither(string environment)
    {
        var http = Client(environment);
        foreach (var path in new[] { "/swagger", "/swagger/index.html", "/swagger/v1/swagger.json", "/openapi/v1.json" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode); // the app itself is fine
    }
}
