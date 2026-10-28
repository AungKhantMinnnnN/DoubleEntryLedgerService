using Ledger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Api.Data;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<AccountBalance> AccountBalances => Set<AccountBalance>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<JournalEntry>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            // One reversal per entry (NULLs are distinct, so ordinary entries are unaffected).
            e.HasIndex(x => x.ReversesEntryId).IsUnique();
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Restrict); // entries are immutable; else EF invents a shadow JournalEntryId
        });
        b.Entity<JournalLine>(e =>
        {
            e.Property(x => x.Direction).HasConversion<string>();
            // Serves GET /accounts/{id}/transactions: filter by account, newest first, keyset on (PostedAt, Id).
            e.HasIndex(x => new { x.AccountId, x.PostedAt, x.Id }).IsDescending(false, true, true);
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.OriginalAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.FxRate).HasColumnType("numeric(19,8)");
        });
        b.Entity<AccountBalance>(e =>
        {
            e.HasKey(x => x.AccountId);
            e.Property(x => x.Balance).HasColumnType("numeric(19,4)");
            e.Property(x => x.Version).IsRowVersion(); // xmin
        });
        b.Entity<FxRate>(e =>
        {
            e.Property(x => x.Rate).HasColumnType("numeric(19,8)");
            e.HasIndex(x => new { x.Base, x.Quote, x.EffectiveAt });
        });
        b.Entity<OutboxMessage>().HasIndex(x => x.CreatedAt).HasFilter("\"PublishedAt\" IS NULL"); // the publisher's poll
    }
}
