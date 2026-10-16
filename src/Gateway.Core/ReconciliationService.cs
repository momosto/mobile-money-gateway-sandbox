using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gateway.Core;

public record ReconcileSummary(int Checked, int Settled, int FailedNeverReceived, int StillPending);

/// <summary>
/// Settles payments the callback never settled: lost callbacks, provider timeouts and
/// crashes mid-submit. Several workers can run at once; FOR UPDATE SKIP LOCKED gives each
/// a different batch.
/// </summary>
public class ReconciliationService(GatewayDbContext db, IProviderClient provider, PaymentService payments, ILogger<ReconciliationService> log)
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);
    public const int BatchSize = 50;

    /// <param name="quietPeriod">How long a payment must sit untouched before it is checked; gives the callback time to arrive.</param>
    public async Task<ReconcileSummary> RunOnceAsync(CancellationToken ct, TimeSpan? quietPeriod = null)
    {
        var cutoff = DateTimeOffset.UtcNow - (quietPeriod ?? QuietPeriod);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Payments.FromSql($"""
            SELECT * FROM payments
            WHERE status IN ('Pending', 'Unknown', 'Submitting') AND updated_at < {cutoff}
            ORDER BY updated_at
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        int settled = 0, neverReceived = 0, pending = 0;
        foreach (var payment in batch)
        {
            payment.ReconcileAttempts++;
            payment.UpdatedAt = DateTimeOffset.UtcNow;
            ChargeStatus? status;
            try
            {
                status = await provider.GetStatusAsync(PaymentService.Reference(payment.Id), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Provider unreachable: leave this payment as it is and try again next run.
                log.LogWarning(e, "Couldn't reach the provider for payment {Id}; will retry", payment.Id);
                pending++;
                continue;
            }

            if (status is null)
            {
                if (payment.Status is PaymentStatus.Unknown or PaymentStatus.Submitting)
                {
                    // The provider has no record, so the customer was never charged. Safe to fail.
                    payment.Status = PaymentStatus.Failed;
                    payment.FailureReason = "provider has no record of the charge; not charged";
                    neverReceived++;
                }
                else pending++;
                continue;
            }

            await payments.ApplyAsync(payment, status, ct);
            if (Payment.IsFinal(payment.Status)) settled++; else pending++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (batch.Count > 0)
            log.LogInformation("Reconciled {Count}: {Settled} settled, {Never} never reached the provider, {Pending} still pending", batch.Count, settled, neverReceived, pending);
        return new(batch.Count, settled, neverReceived, pending);
    }
}
