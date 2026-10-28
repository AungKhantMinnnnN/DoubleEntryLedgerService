using Confluent.Kafka;
using Ledger.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Api;

public class PostgresCheck(LedgerDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("postgres unreachable");
}

public class KafkaCheck(string bootstrapServers) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
            admin.GetMetadata(TimeSpan.FromSeconds(2)); // throws if no broker answers
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (KafkaException e)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("kafka unreachable", e));
        }
    }
}
