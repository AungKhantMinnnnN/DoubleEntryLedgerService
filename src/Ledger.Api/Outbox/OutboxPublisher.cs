using System.Runtime.ExceptionServices;
using Ledger.Api.Data;
using Ledger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Outbox;

public class OutboxPublisher(IServiceScopeFactory scopes, IEventSink sink, ILogger<OutboxPublisher> log) : BackgroundService
{
    // Large enough that the producer can batch network requests; the load test showed 100 sequential sends cap out near 140 events/s.
    const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                using var scope = scopes.CreateScope();
                sent = await PublishBatchAsync(scope.ServiceProvider.GetRequiredService<LedgerDbContext>(), sink, BatchSize, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning(e, "outbox publish failed, will retry"); }

            if (sent == 0)
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // Claims a batch with FOR UPDATE SKIP LOCKED (so several publishers never take the same row), sends it,
    // and marks the sent rows in the same transaction. A crash between send and commit re-sends the batch:
    // at-least-once, never lost.
    //
    // The whole batch is handed to the sink at once so the producer can batch requests (awaiting each message
    // before sending the next costs a broker round trip per event). Acknowledgements are then read in order and
    // only the unbroken run of successes from the start is marked published. If message N fails, N and everything
    // after it stay pending even if some were delivered; they are re-sent later (duplicates, never gaps, and
    // events keep their order). The failure is rethrown after the prefix is committed.
    public static async Task<int> PublishBatchAsync(LedgerDbContext db, IEventSink sink, int batchSize, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.Outbox.FromSql($"""
            SELECT * FROM "Outbox" WHERE "PublishedAt" IS NULL
            ORDER BY "CreatedAt", "Id" LIMIT {batchSize} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        var sends = batch.Select(m => Start(sink, m, ct)).ToList();

        var sent = 0;
        Exception? failure = null;
        foreach (var (m, send) in batch.Zip(sends))
        {
            try { await send; }
            catch (Exception e) { failure = e; break; }
            m.PublishedAt = DateTimeOffset.UtcNow;
            sent++;
        }
        // Let in-flight sends finish (and observe their errors) before the transaction closes.
        try { await Task.WhenAll(sends); } catch { /* already handled via the ordered loop above */ }

        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return sent;
    }

    // A sink that throws synchronously must not abort the loop that starts the other sends.
    static Task Start(IEventSink sink, OutboxMessage m, CancellationToken ct)
    {
        try { return sink.PublishAsync(m, ct); }
        catch (Exception e) { return Task.FromException(e); }
    }
}
