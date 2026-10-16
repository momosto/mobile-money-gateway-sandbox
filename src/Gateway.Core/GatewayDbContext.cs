using Microsoft.EntityFrameworkCore;

namespace Gateway.Core;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var p = b.Entity<Payment>();
        p.ToTable("payments");
        p.HasKey(x => x.Id);
        p.Property(x => x.Id).HasColumnName("id");
        p.Property(x => x.MerchantId).HasColumnName("merchant_id").HasMaxLength(64);
        p.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        p.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        p.Property(x => x.Msisdn).HasColumnName("msisdn").HasMaxLength(20);
        p.Property(x => x.AmountCents).HasColumnName("amount_cents");
        p.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
        p.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        p.Property(x => x.ProviderReference).HasColumnName("provider_reference").HasMaxLength(64);
        p.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(200);
        p.Property(x => x.ReconcileAttempts).HasColumnName("reconcile_attempts");
        p.Property(x => x.CreatedAt).HasColumnName("created_at");
        p.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // The database, not application code, guarantees one payment per merchant + key.
        p.HasIndex(x => new { x.MerchantId, x.IdempotencyKey }).IsUnique();
        // The reconciliation worker scans by status and age.
        p.HasIndex(x => new { x.Status, x.UpdatedAt });
    }
}
