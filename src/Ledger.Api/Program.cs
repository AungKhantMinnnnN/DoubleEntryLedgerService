using Ledger.Api;
using Ledger.Api.Data;
using Ledger.Api.Features;
using Ledger.Api.Outbox;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// One JSON line per log event (with scopes, so the correlation id is on every line) outside local development.
if (!builder.Environment.IsDevelopment())
    builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.AddDbContext<LedgerDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Ledger")));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
if (builder.Environment.IsDevelopment())
    builder.Services.AddOpenApi(o => o.AddOperationTransformer((op, _, _) =>
    {
        // The header is bound as optional so the handler can answer with a problem+json 400; document it as required.
        foreach (var p in op.Parameters?.OfType<Microsoft.OpenApi.OpenApiParameter>() ?? [])
            if (p.Name == "Idempotency-Key") p.Required = true;
        return Task.CompletedTask;
    })); // errors, including unhandled exceptions, are RFC 9457 application/problem+json

var health = builder.Services.AddHealthChecks().AddCheck<PostgresCheck>("postgres", tags: ["ready"]);

// Publishing only runs when Kafka is configured (not in tests or a bare dev run).
if (builder.Configuration["Kafka:BootstrapServers"] is { Length: > 0 } kafka)
{
    var topic = builder.Configuration["Kafka:Topic"] ?? "ledger.entries";
    builder.Services.AddSingleton<IEventSink>(_ => new KafkaSink(kafka, topic));
    builder.Services.AddHostedService<OutboxPublisher>();
    health.AddCheck("kafka", new KafkaCheck(kafka), tags: ["ready"]);
}

var app = builder.Build();

// Migrations run with their own (owner) connection so the service itself can use the unprivileged ledger_app role.
if (app.Configuration.GetConnectionString("Migrations") is { Length: > 0 } migrations)
{
    using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(migrations).Options);
    db.Database.Migrate();
}

// Correlation id: take the caller's, or make one; echo it and put it on every log line of the request.
app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() is { Length: > 0 and <= 100 } given ? given : Guid.NewGuid().ToString("N");
    // OnStarting, not a plain assignment: the exception handler clears headers set earlier.
    ctx.Response.OnStarting(() => { ctx.Response.Headers["X-Correlation-Id"] = id; return Task.CompletedTask; });
    using (ctx.RequestServices.GetRequiredService<ILogger<Program>>().BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        await next();
});

app.UseExceptionHandler();
app.UseStatusCodePages();

// API docs are a development aid only: not registered at all in any other environment, so the routes 404.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                                                     // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Ledger API")); // /swagger
}

app.MapGet("/health", () => Results.Ok("ok")); // liveness: the process is up, no dependencies
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") }); // readiness: Postgres (+ Kafka if configured)
app.MapLedger();

app.Run();

public partial class Program; // for WebApplicationFactory in tests
