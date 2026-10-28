using System.Text.Json;
using Ledger.Api.Data;
using Ledger.Api.Domain;

namespace Ledger.Api.Features;

public static class Audit
{
    // Adds the row to the caller's unit of work, so it commits or rolls back with the change it describes.
    public static void Record(LedgerDbContext db, string action, object payload) =>
        db.AuditLog.Add(new AuditLog { Action = action, Payload = JsonSerializer.Serialize(payload) });
}
