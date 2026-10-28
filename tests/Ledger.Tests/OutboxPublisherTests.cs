using Ledger.Api.Domain;
using Ledger.Api.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Tests;

public class OutboxPublisherTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // Records deliveries; can be told to fail on the Nth call (simulating a broker error or a crash).
    class FakeSink(int? failOnCall = null, bool deliverBeforeFailing = false) : IEventSink
    {
        public readonly List<Guid> Delivered = [];
        int _calls;
        public Task PublishAsync(OutboxMessage m, CancellationToken ct)
        {
            lock (Delivered)
            {
                if (++_calls == failOnCall)
                {
                    if (deliverBeforeFailing) Delivered.Add(m.Id); // broker got it, but the publisher never learned
                    throw new IOException("boom");
                }
                Delivered.Add(m.Id);
            }
            return Task.CompletedTask;
        }
    }

    async Task<List<Guid>> Seed(int n)
    {
        await using var db = pg.NewContext();
        var rows = Enumerable.Range(0, n).Select(i => new OutboxMessage
        {
            Key = "k", Type = "EntryPosted", Payload = "{}", CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(i),
        }).ToList();
        db.Outbox.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    }

    async Task<int> Publish(IEventSink sink, int batch = 10)
    {
        await using var db = pg.NewContext();
        return await OutboxPublisher.PublishBatchAsync(db, sink, batch, default);
    }

    async Task<int> Unpublished(List<Guid> ids)
    {
        await using var db = pg.NewContext();
        return await db.Outbox.CountAsync(o => ids.Contains(o.Id) && o.PublishedAt == null);
    }

    [Fact]
    public async Task Publishes_each_row_once_and_marks_it()
    {
        var ids = await Seed(25);
        var sink = new FakeSink();
        while (await Publish(sink) > 0) { }
        Assert.Equal(25, sink.Delivered.Count(ids.Contains));
        Assert.Equal(0, await Unpublished(ids));
    }

    [Fact]
    public async Task Sink_failure_keeps_unsent_rows_and_commits_the_sent_ones()
    {
        var ids = await Seed(5);
        var sink = new FakeSink(failOnCall: 3);
        await Assert.ThrowsAsync<IOException>(() => Publish(sink));
        Assert.Equal(3, await Unpublished(ids)); // rows 3..5 still pending (rows 1,2 were sent and marked)

        var healthy = new FakeSink();
        while (await Publish(healthy) > 0) { }
        Assert.Equal(0, await Unpublished(ids));
    }

    [Fact]
    public async Task Publisher_dying_mid_batch_loses_no_events()
    {
        var ids = await Seed(20);
        var all = new List<Guid>();

        // "Kill" the publisher while the broker has accepted a message it never recorded as sent.
        var dying = new FakeSink(failOnCall: 7, deliverBeforeFailing: true);
        await Assert.ThrowsAsync<IOException>(() => Publish(dying, 20));
        all.AddRange(dying.Delivered);
        Assert.Equal(14, await Unpublished(ids)); // only the unbroken prefix (rows 1-6) was marked; 7-20 stay pending

        var restarted = new FakeSink();
        while (await Publish(restarted) > 0) { }
        all.AddRange(restarted.Delivered);

        var mine = all.Where(ids.Contains).ToList();
        Assert.Equal(ids.Count, mine.Distinct().Count());   // nothing lost
        Assert.True(mine.Count >= ids.Count);               // duplicates allowed: at-least-once
        // The sink received the whole batch (so 7-20 were delivered once already) but only 1-6 were acknowledged
        // in order, so 7-20 are re-sent: gaps are impossible, duplicates are the price.
        Assert.Equal(14, mine.Count - mine.Distinct().Count());
        Assert.Equal(0, await Unpublished(ids));
    }

    [Fact]
    public async Task Concurrent_publishers_never_send_the_same_row_twice()
    {
        var ids = await Seed(200);
        var sink = new FakeSink();
        async Task Worker() { while (await Publish(sink, 7) > 0) { } }
        await Task.WhenAll(Worker(), Worker(), Worker());

        var mine = sink.Delivered.Where(ids.Contains).ToList();
        Assert.Equal(200, mine.Count);
        Assert.Equal(200, mine.Distinct().Count());
    }

    // Sends one message at a time would be at most one in flight; the batch must be handed over all at once.
    class SlowSink : IEventSink
    {
        int _inFlight;
        public int MaxInFlight;
        public async Task PublishAsync(OutboxMessage m, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    [Fact]
    public async Task Whole_batch_is_in_flight_at_once()
    {
        var ids = await Seed(50);
        var sink = new SlowSink();
        Assert.Equal(50, await Publish(sink, 50));
        Assert.Equal(50, sink.MaxInFlight);
        Assert.Equal(0, await Unpublished(ids));
    }
}
