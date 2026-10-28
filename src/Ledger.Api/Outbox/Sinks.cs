using Confluent.Kafka;
using Ledger.Api.Domain;

namespace Ledger.Api.Outbox;

// Two implementations: Kafka in production, a recording fake in tests.
public interface IEventSink
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

public sealed class KafkaSink(string bootstrapServers, string topic) : IEventSink, IDisposable
{
    readonly IProducer<string, string> _producer = new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = bootstrapServers,
        Acks = Acks.All,
        EnableIdempotence = true,   // no duplicates or reordering from producer-side retries
        MessageTimeoutMs = 10_000,  // fail fast so the row stays unpublished and is retried by the publisher
    }).Build();

    public async Task PublishAsync(OutboxMessage m, CancellationToken ct) =>
        await _producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = m.Key,
            Value = m.Payload,
            // Consumers deduplicate on event-id (delivery is at-least-once).
            Headers = [new Header("event-id", System.Text.Encoding.UTF8.GetBytes(m.Id.ToString())), new Header("event-type", System.Text.Encoding.UTF8.GetBytes(m.Type))],
        }, ct);

    public void Dispose() { _producer.Flush(TimeSpan.FromSeconds(5)); _producer.Dispose(); }
}
