using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gateway.Core;

public record CreatePayment(string Msisdn, long AmountCents, string Currency = "USD");

public enum CreateOutcome { Created, Replayed, KeyReusedWithDifferentRequest }

public record CreateResult(CreateOutcome Outcome, Payment? Payment);

public class PaymentService(GatewayDbContext db, IProviderClient provider, GatewayOptions options, ILogger<PaymentService> log)
{
    /// <summary>
    /// Records the payment and charges the customer at most once, however many identical
    /// requests arrive at the same moment.
    ///
    /// 1. INSERT ... ON CONFLICT DO NOTHING against a unique (merchant, key) index. Exactly
    ///    one concurrent request gets a row back; the others get nothing and replay it.
    /// 2. The winner claims the payment with UPDATE ... WHERE status = 'Created'. Only a
    ///    claimed payment is ever sent to the provider.
    /// </summary>
    public async Task<CreateResult> CreateAsync(string merchantId, string idempotencyKey, CreatePayment request, CancellationToken ct)
    {
        var hash = Hash($"{request.Msisdn}|{request.AmountCents}|{request.Currency}");
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, msisdn, amount_cents, currency, status, reconcile_attempts, created_at, updated_at)
            VALUES ({id}, {merchantId}, {idempotencyKey}, {hash}, {request.Msisdn}, {request.AmountCents}, {request.Currency}, {nameof(PaymentStatus.Created)}, 0, {now}, {now})
            ON CONFLICT (merchant_id, idempotency_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);

        if (inserted.Count == 0)
        {
            var existing = await db.Payments.AsNoTracking().SingleAsync(p => p.MerchantId == merchantId && p.IdempotencyKey == idempotencyKey, ct);
            return existing.RequestHash == hash
                ? new(CreateOutcome.Replayed, existing)
                : new(CreateOutcome.KeyReusedWithDifferentRequest, existing);
        }

        if (!await TransitionAsync(id, PaymentStatus.Created, PaymentStatus.Submitting, ct))
            throw new InvalidOperationException($"Payment {id} was claimed by another process right after it was inserted.");

        var result = await provider.SubmitAsync(
            new ChargeRequest(Reference(id), request.Msisdn, request.AmountCents, request.Currency, options.CallbackUrl), ct);

        var (next, reason) = result.Outcome switch
        {
            SubmitOutcome.Accepted => (PaymentStatus.Pending, (string?)null),
            SubmitOutcome.Declined => (PaymentStatus.Failed, result.Reason),
            _ => (PaymentStatus.Unknown, result.Reason),
        };
        await db.Payments.Where(p => p.Id == id && p.Status == PaymentStatus.Submitting).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, next)
            .SetProperty(p => p.ProviderReference, result.ProviderReference)
            .SetProperty(p => p.FailureReason, reason)
            .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), ct);

        log.LogInformation("Payment {Id} submitted: {Outcome}", id, result.Outcome);
        return new(CreateOutcome.Created, await db.Payments.AsNoTracking().SingleAsync(p => p.Id == id, ct));
    }

    /// <summary>Moves a payment from one status to another only if it is still in the expected one.</summary>
    public async Task<bool> TransitionAsync(Guid id, PaymentStatus from, PaymentStatus to, CancellationToken ct) =>
        await db.Payments.Where(p => p.Id == id && p.Status == from).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, to)
            .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow), ct) == 1;

    /// <summary>
    /// Applies the provider's final word (from a callback or a status query). Safe to call
    /// twice: the row is locked, and a payment that is already final is left alone.
    /// </summary>
    public async Task<Payment?> SettleAsync(ChargeStatus status, CancellationToken ct)
    {
        if (!TryParseReference(status.Reference, out var id)) return null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payment = await db.Payments.FromSql($"SELECT * FROM payments WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (payment is null) return null;

        if (!Payment.IsFinal(payment.Status))
            await ApplyAsync(payment, status, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return payment;
    }

    /// <summary>Applies a provider status to a payment the caller has already locked.</summary>
    internal async Task ApplyAsync(Payment payment, ChargeStatus status, CancellationToken ct)
    {
        payment.ProviderReference ??= status.ProviderReference;
        payment.UpdatedAt = DateTimeOffset.UtcNow;
        switch (status.Status)
        {
            case "succeeded" when status.AmountCents != payment.AmountCents:
                // The provider took a different amount than we asked for: give it back.
                var reversed = await provider.ReverseAsync(status.Reference, ct);
                payment.Status = reversed ? PaymentStatus.Reversed : PaymentStatus.Unknown;
                payment.FailureReason = $"provider settled {status.AmountCents} cents, expected {payment.AmountCents}" + (reversed ? "; reversed" : "; reversal failed, retrying");
                log.LogWarning("Payment {Id}: amount mismatch, reversal {Result}", payment.Id, reversed ? "succeeded" : "failed");
                break;
            case "succeeded":
                payment.Status = PaymentStatus.Succeeded;
                payment.FailureReason = null;
                break;
            case "failed":
                payment.Status = PaymentStatus.Failed;
                payment.FailureReason = status.Reason ?? "declined by provider";
                break;
            // "pending": nothing to do yet.
        }
    }

    public static string Reference(Guid id) => id.ToString("N");

    public static bool TryParseReference(string reference, out Guid id) => Guid.TryParseExact(reference, "N", out id);

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>HMAC-SHA256 of the raw callback body, hex encoded, compared in constant time.</summary>
    public static bool SignatureIsValid(string body, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        var expected = Sign(body, secret);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    public static string Sign(string body, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

public class GatewayOptions
{
    public string CallbackUrl { get; set; } = "http://localhost:5080/webhooks/provider";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Merchant API keys: key -> merchant id.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
}
