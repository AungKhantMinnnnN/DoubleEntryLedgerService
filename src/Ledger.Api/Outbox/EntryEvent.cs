using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Domain;
using Ledger.Api.Features;

namespace Ledger.Api.Outbox;

// Event contract v1 (documented in the README). Additive changes only; breaking changes bump Version.
public record EntryEvent(Guid EventId, string Type, int Version, DateTimeOffset OccurredAt, Guid EntryId,
    Guid? ReversesEntryId, string? Description, string? Reference, List<EntryLineDto> Lines);

public static class OutboxEvents
{
    public const string Posted = "EntryPosted", Reversed = "EntryReversed";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Built from the same DTO the API returns, so events and responses never disagree.
    public static OutboxMessage Create(JournalEntry entry, string type)
    {
        var id = Guid.NewGuid();
        var dto = JournalEntries.ToDto(entry);
        var evt = new EntryEvent(id, type, 1, dto.PostedAt, dto.Id, dto.ReversesEntryId, dto.Description, dto.Reference, dto.Lines);
        // ponytail: keyed by entry id, so ordering holds per entry only; key by account if per-account ordering is needed.
        return new OutboxMessage { Id = id, Key = entry.Id.ToString(), Type = type, Payload = JsonSerializer.Serialize(evt, Json) };
    }
}
