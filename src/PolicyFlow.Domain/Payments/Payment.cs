using PolicyFlow.Domain.Common;

namespace PolicyFlow.Domain.Payments;

public enum PaymentStatus
{
    Approved = 0,
    Declined = 1
}

/// <summary>
/// A premium payment attempt against a policy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IdempotencyKey"/> carries a UNIQUE index in SQL Server. That index,
/// not application logic, is what makes duplicate submission safe: if two requests
/// race past the "does this key exist" read, the second INSERT violates the
/// constraint and the handler returns the original payment instead of creating a
/// second transaction.
/// </para>
/// <para>
/// No card data, bank details or gateway credentials exist anywhere in this type.
/// The POC records an amount, a status and a simulated gateway reference — nothing
/// that would be in scope for PCI DSS.
/// </para>
/// </remarks>
public class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }

    public Guid PolicyId { get; private set; }

    public decimal Amount { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// Client-supplied key from the <c>Idempotency-Key</c> request header. Unique
    /// per policy; see the index configuration in the infrastructure layer.
    /// </summary>
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>
    /// Reference returned by the simulated payment gateway. Safe to log: it is a
    /// synthetic identifier, not a card token or account number.
    /// </summary>
    public string ExternalTransactionId { get; private set; } = null!;

    public string CorrelationId { get; private set; } = null!;

    public DateTime CreatedUtc { get; private set; }

    public Policies.Policy? Policy { get; private set; }

    /// <summary>Records an approved payment.</summary>
    public static Payment Approve(
        Guid policyId,
        decimal amount,
        string idempotencyKey,
        string externalTransactionId,
        string correlationId,
        DateTime nowUtc)
    {
        if (amount <= 0m)
        {
            throw new DomainException("Payment amount must be greater than zero.");
        }

        if (amount > 100_000m)
        {
            throw new DomainException("Payment amount exceeds the demo transaction limit.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("An Idempotency-Key is required to submit a payment.");
        }

        return new Payment
        {
            Id = Guid.NewGuid(),
            PolicyId = policyId,
            Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            Status = PaymentStatus.Approved,
            IdempotencyKey = idempotencyKey.Trim(),
            ExternalTransactionId = externalTransactionId,
            CorrelationId = correlationId,
            CreatedUtc = nowUtc
        };
    }
}
