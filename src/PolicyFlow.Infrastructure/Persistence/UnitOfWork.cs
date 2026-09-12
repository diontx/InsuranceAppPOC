using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Payments;
using PolicyFlow.Domain.Common;

namespace PolicyFlow.Infrastructure.Persistence;

/// <summary>
/// Transaction boundary over <see cref="PolicyFlowDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Also the translation point for provider-specific failures. A SQL Server error
/// number means nothing to the application layer, so unique-constraint violations
/// are converted into the domain-meaningful exceptions the handlers catch. Without
/// this, either every handler would need to know about error 2601, or a duplicate
/// payment would surface to the customer as an opaque 500.
/// </para>
/// </remarks>
public sealed class UnitOfWork(
    PolicyFlowDbContext db,
    ILogger<UnitOfWork> logger) : IUnitOfWork
{
    /// <summary>SQL Server: attempt to insert a duplicate key in a unique index.</summary>
    private const int UniqueIndexViolation = 2601;

    /// <summary>SQL Server: violation of a UNIQUE KEY constraint.</summary>
    private const int UniqueConstraintViolation = 2627;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && IsUniqueViolation(sql))
        {
            throw TranslateUniqueViolation(sql, ex);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A rowversion mismatch: another transaction changed the row after we
            // read it. Surfaced as a 409 so the caller re-reads and retries rather
            // than overwriting the other change.
            logger.LogWarning(
                ex,
                "Optimistic concurrency conflict. Operation={Operation} Result={Result}",
                "SaveChanges", "ConcurrencyConflict");

            throw new BusinessConflictException(
                "This record was modified by another operation. Reload it and try again.");
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> in an explicit transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation is wrapped in the provider's execution strategy. This is
    /// mandatory, not optional: the SQL Server provider is configured with
    /// <c>EnableRetryOnFailure</c>, and a retrying strategy refuses a
    /// user-initiated transaction unless the whole transaction is inside the
    /// retriable unit. Otherwise EF Core throws
    /// "SqlServerRetryingExecutionStrategy does not support user-initiated
    /// transactions" on every call.
    /// </para>
    /// <para>
    /// It is also the correct semantics. If a transient fault aborts the
    /// transaction, the strategy retries the <i>entire</i> operation — reads,
    /// writes and commit together — rather than retrying a commit on a
    /// transaction that has already rolled back. <b>That is why
    /// <paramref name="operation"/> must be idempotent:</b> it can run more than
    /// once. The callers here satisfy that, because a retry re-reads and re-derives
    /// everything inside the delegate.
    /// </para>
    /// <para>
    /// Nested calls join the ambient transaction rather than opening a second one,
    /// so a handler calling another handler does not deadlock against itself. The
    /// in-memory provider has no transactions, so the operation simply runs —
    /// unit tests verify logic, integration tests verify transactionality against
    /// real SQL Server.
    /// </para>
    /// </remarks>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (db.Database.CurrentTransaction is not null || !db.Database.IsRelational())
        {
            return await operation(ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            try
            {
                var result = await operation(token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch
            {
                // Roll back explicitly rather than relying on dispose, so the
                // rollback is awaited and a rollback failure is not swallowed at
                // dispose time.
                await transaction.RollbackAsync(token);
                throw;
            }
        }, ct);
    }

    private static bool IsUniqueViolation(SqlException sql) =>
        sql.Number is UniqueIndexViolation or UniqueConstraintViolation;

    /// <summary>
    /// Maps a unique-violation to the exception the application layer understands,
    /// using the index name in the SQL Server error message.
    /// </summary>
    private static Exception TranslateUniqueViolation(SqlException sql, DbUpdateException original)
    {
        var message = sql.Message;

        if (message.Contains("UX_Payments_PolicyId_IdempotencyKey", StringComparison.OrdinalIgnoreCase))
        {
            return new DuplicateIdempotencyKeyException("(scoped to policy)", original);
        }

        if (message.Contains("UX_Policies_QuoteId", StringComparison.OrdinalIgnoreCase))
        {
            return new BusinessConflictException("This quote is already bound to a policy.");
        }

        if (message.Contains("UX_Policies_PolicyNumber", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("UX_Quotes_QuoteNumber", StringComparison.OrdinalIgnoreCase))
        {
            return new BusinessConflictException(
                "A number collision occurred while creating this record. Please retry.");
        }

        if (message.Contains("UX_CarrierOffers_Quote_Carrier_Tier", StringComparison.OrdinalIgnoreCase))
        {
            return new BusinessConflictException(
                "This quote already has an offer from that carrier and coverage tier.");
        }

        if (message.Contains("PK_ProcessedMessages", StringComparison.OrdinalIgnoreCase))
        {
            // Two workers raced on the same message. The loser's work is redundant,
            // not wrong; the caller treats this as an already-processed message.
            return new DuplicateProcessedMessageException(original);
        }

        // An unrecognised unique index. Do not guess — surface it as a conflict
        // with the original exception intact for troubleshooting.
        return new BusinessConflictException(
            "The operation violated a uniqueness constraint. See logs for details.");
    }
}

/// <summary>
/// Two consumers processed the same message concurrently and both tried to record
/// it. Expected under at-least-once delivery; the loser discards its work.
/// </summary>
public sealed class DuplicateProcessedMessageException(Exception inner)
    : Exception("This message has already been recorded as processed by this consumer.", inner);
