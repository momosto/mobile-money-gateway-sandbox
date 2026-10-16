namespace Gateway.Core;

/// <summary>
/// Where a payment is in its life. Every change of status goes through a conditional
/// UPDATE (WHERE status = expected), so two processes can never both move it.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Recorded, not yet sent to the provider.</summary>
    Created,
    /// <summary>Claimed by exactly one request, which is sending it to the provider now.</summary>
    Submitting,
    /// <summary>The provider accepted it; waiting for its callback or for reconciliation.</summary>
    Pending,
    /// <summary>The provider call timed out or errored, so we don't know if it charged. Never resent blindly.</summary>
    Unknown,
    Succeeded,
    Failed,
    /// <summary>The provider settled it wrongly (for example a different amount) and we reversed it.</summary>
    Reversed,
}

public class Payment
{
    public Guid Id { get; set; }
    public string MerchantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    /// <summary>SHA-256 of the request body: the same key with a different body is rejected.</summary>
    public string RequestHash { get; set; } = "";
    public string Msisdn { get; set; } = "";
    public long AmountCents { get; set; }
    public string Currency { get; set; } = "USD";
    public PaymentStatus Status { get; set; }
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }
    public int ReconcileAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static bool IsFinal(PaymentStatus s) => s is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Reversed;
}
