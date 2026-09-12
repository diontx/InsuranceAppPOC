using Microsoft.EntityFrameworkCore;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Messaging;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Policies;
using PolicyFlow.Infrastructure.Persistence;

namespace PolicyFlow.Infrastructure.Repositories;

public sealed class PolicyRepository(PolicyFlowDbContext db) : IPolicyRepository
{
    public Task<Policy?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Policies.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Policy?> GetWithPaymentsAsync(Guid id, CancellationToken ct = default) =>
        db.Policies
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Policy?> GetByPolicyNumberAsync(string policyNumber, CancellationToken ct = default) =>
        db.Policies
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.PolicyNumber == policyNumber, ct);

    /// <summary>
    /// Reads the policy already bound from this quote, if any. Uses the UNIQUE
    /// index on QuoteId, so this is a single seek.
    /// </summary>
    public Task<Policy?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default) =>
        db.Policies.FirstOrDefaultAsync(p => p.QuoteId == quoteId, ct);

    public async Task AddAsync(Policy policy, CancellationToken ct = default) =>
        await db.Policies.AddAsync(policy, ct);

    public async Task<string> NextPolicyNumberAsync(int year, CancellationToken ct = default) =>
        $"POL-{year}-{await QuoteRepository.NextValueAsync(db, "Policy", year, ct):D6}";
}

public sealed class PaymentRepository(PolicyFlowDbContext db) : IPaymentRepository
{
    /// <summary>
    /// Looks up a prior payment for this idempotency key on this policy. Backed by
    /// the UNIQUE index <c>UX_Payments_PolicyId_IdempotencyKey</c>, so it is a seek
    /// on exactly the columns the constraint covers.
    /// </summary>
    public Task<Payment?> FindByIdempotencyKeyAsync(
        Guid policyId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(
            p => p.PolicyId == policyId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct = default) =>
        await db.Payments.AddAsync(payment, ct);
}

public sealed class OutboxRepository(PolicyFlowDbContext db) : IOutboxRepository
{
    public async Task AddAsync(OutboxMessage message, CancellationToken ct = default) =>
        await db.OutboxMessages.AddAsync(message, ct);

    /// <summary>
    /// Oldest unsent messages first. Matches the filtered index
    /// <c>IX_OutboxMessages_ProcessedUtc_OccurredUtc</c>, so processed rows are not
    /// scanned at all.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(
        int batchSize,
        CancellationToken ct = default) =>
        await db.OutboxMessages
            .Where(m => m.ProcessedUtc == null)
            .OrderBy(m => m.OccurredUtc)
            .Take(batchSize)
            .ToListAsync(ct);
}

public sealed class ProcessedMessageRepository(PolicyFlowDbContext db) : IProcessedMessageRepository
{
    /// <summary>
    /// Primary-key seek on <c>(MessageId, Consumer)</c>. This is the consumer-side
    /// half of at-least-once delivery handling.
    /// </summary>
    public Task<bool> HasBeenProcessedAsync(Guid messageId, string consumer, CancellationToken ct = default) =>
        db.ProcessedMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == consumer, ct);

    public async Task AddAsync(ProcessedMessage processedMessage, CancellationToken ct = default) =>
        await db.ProcessedMessages.AddAsync(processedMessage, ct);
}
