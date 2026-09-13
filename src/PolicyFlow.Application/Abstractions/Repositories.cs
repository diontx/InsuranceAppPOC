using PolicyFlow.Domain.Messaging;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Policies;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Abstractions;

/// <summary>
/// Persistence port for the quote aggregate. The application layer talks to this
/// interface, never to EF Core, so use cases stay testable without a database and
/// the storage technology stays swappable.
/// </summary>
public interface IQuoteRepository
{
    Task<Quote?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads the quote with its carrier offers eagerly.</summary>
    Task<Quote?> GetWithOffersAsync(Guid id, CancellationToken ct = default);

    Task<Quote?> GetByQuoteNumberAsync(string quoteNumber, CancellationToken ct = default);

    /// <summary>
    /// Mobile self-service lookup: quote number plus a surname the caller already
    /// knows. Deliberately not a full customer search.
    /// </summary>
    Task<Quote?> FindForSelfServiceLookupAsync(string quoteNumber, string lastName, CancellationToken ct = default);

    /// <summary>
    /// The underwriter work queue: quotes in <c>Referral</c> or <c>FollowUp</c>,
    /// oldest first so the longest-waiting applicant is reviewed first. Read-only
    /// and without offers, because a queue row needs neither.
    /// </summary>
    Task<IReadOnlyList<Quote>> GetAwaitingUnderwriterAsync(int maxResults, CancellationToken ct = default);

    /// <summary>Quotes one agent submitted, newest first, with offers for the count.</summary>
    Task<IReadOnlyList<Quote>> GetSubmittedByAsync(string submittedBy, int maxResults, CancellationToken ct = default);

    Task AddAsync(Quote quote, CancellationToken ct = default);

    /// <summary>Allocates the next sequential quote number, e.g. Q-2026-000042.</summary>
    Task<string> NextQuoteNumberAsync(int year, CancellationToken ct = default);
}

public interface IPolicyRepository
{
    Task<Policy?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads the policy with its payment history eagerly.</summary>
    Task<Policy?> GetWithPaymentsAsync(Guid id, CancellationToken ct = default);

    Task<Policy?> GetByPolicyNumberAsync(string policyNumber, CancellationToken ct = default);

    /// <summary>
    /// Finds an existing policy bound from this quote. The read is an optimisation
    /// for a clean 409; the unique index on QuoteId is the actual guarantee.
    /// </summary>
    Task<Policy?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default);

    Task AddAsync(Policy policy, CancellationToken ct = default);

    Task<string> NextPolicyNumberAsync(int year, CancellationToken ct = default);
}

public interface IPaymentRepository
{
    /// <summary>
    /// Looks up a prior payment by its idempotency key within one policy. Returning
    /// the original payment is what makes a retried request safe.
    /// </summary>
    Task<Payment?> FindByIdempotencyKeyAsync(Guid policyId, string idempotencyKey, CancellationToken ct = default);

    Task AddAsync(Payment payment, CancellationToken ct = default);
}

/// <summary>
/// Staging port for the transactional outbox. Events written here participate in
/// the caller's transaction, so "data saved" and "event will publish" commit
/// together or not at all.
/// </summary>
public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);

    /// <summary>Oldest unsent messages first, capped by <paramref name="batchSize"/>.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize, CancellationToken ct = default);
}

/// <summary>Consumer-side deduplication port for at-least-once message delivery.</summary>
public interface IProcessedMessageRepository
{
    Task<bool> HasBeenProcessedAsync(Guid messageId, string consumer, CancellationToken ct = default);

    Task AddAsync(ProcessedMessage processedMessage, CancellationToken ct = default);
}

/// <summary>
/// Transaction boundary. Use cases decide what constitutes one atomic business
/// operation; the infrastructure layer decides how to implement it.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Flushes tracked changes. Throws on a concurrency conflict.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside an explicit database transaction,
    /// committing on success and rolling back on any exception.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
