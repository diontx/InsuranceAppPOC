using Microsoft.EntityFrameworkCore;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Infrastructure.Persistence;

namespace PolicyFlow.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IQuoteRepository"/>.
/// </summary>
/// <remarks>
/// Every query is LINQ, so EF Core parameterises it. The one raw-SQL statement in
/// this class (the number allocator) uses <c>FromSqlInterpolated</c>, which also
/// parameterises — the interpolated values become SQL parameters, not concatenated
/// text.
/// </remarks>
public sealed class QuoteRepository(PolicyFlowDbContext db) : IQuoteRepository
{
    public Task<Quote?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Quotes.FirstOrDefaultAsync(q => q.Id == id, ct);

    public Task<Quote?> GetWithOffersAsync(Guid id, CancellationToken ct = default) =>
        db.Quotes
            .Include(q => q.Offers)
            .FirstOrDefaultAsync(q => q.Id == id, ct);

    public Task<Quote?> GetByQuoteNumberAsync(string quoteNumber, CancellationToken ct = default) =>
        db.Quotes
            .Include(q => q.Offers)
            .FirstOrDefaultAsync(q => q.QuoteNumber == quoteNumber, ct);

    /// <summary>
    /// Mobile self-service lookup. Matches the quote number exactly and requires
    /// the supplied surname to be the last word of the stored customer name.
    /// </summary>
    /// <remarks>
    /// The name check happens in memory on a single row, not in SQL. Quote numbers
    /// are unique, so this reads exactly one row via the unique index; pushing a
    /// surname predicate into SQL would need either a computed column or a
    /// non-SARGable <c>LIKE '%...'</c> that cannot use an index.
    /// </remarks>
    public async Task<Quote?> FindForSelfServiceLookupAsync(
        string quoteNumber,
        string lastName,
        CancellationToken ct = default)
    {
        var quote = await db.Quotes
            .Include(q => q.Offers)
            .FirstOrDefaultAsync(q => q.QuoteNumber == quoteNumber, ct);

        if (quote is null)
        {
            return null;
        }

        var storedLastName = quote.CustomerName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        // Case-insensitive comparison: the customer typing "johnson" should match
        // "Alex Johnson". Returning null rather than the quote on mismatch means a
        // wrong surname is indistinguishable from a wrong quote number, which
        // avoids confirming that a given quote number exists.
        return string.Equals(storedLastName, lastName, StringComparison.OrdinalIgnoreCase)
            ? quote
            : null;
    }

    public async Task AddAsync(Quote quote, CancellationToken ct = default) =>
        await db.Quotes.AddAsync(quote, ct);

    /// <summary>
    /// Allocates the next quote number for the year, e.g. <c>Q-2026-000042</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>UPDLOCK, HOLDLOCK</c> hints take an update lock on the counter row and
    /// hold it to the end of the enclosing transaction. Without them, two concurrent
    /// submissions could both read the same <c>LastValue</c> and both try to claim the
    /// same number — the UNIQUE index would reject the loser and the customer would
    /// see a 500 for no reason they could act on.
    /// </para>
    /// <para>
    /// The caller must already be inside a transaction (<c>SubmitQuoteHandler</c>
    /// is), otherwise the lock is released immediately and provides nothing.
    /// </para>
    /// </remarks>
    public async Task<string> NextQuoteNumberAsync(int year, CancellationToken ct = default) =>
        $"Q-{year}-{await NextValueAsync(db, "Quote", year, ct):D6}";

    internal static async Task<int> NextValueAsync(
        PolicyFlowDbContext db,
        string sequenceName,
        int year,
        CancellationToken ct)
    {
        // In-memory provider (used by unit tests) has no SQL and no locking. Fall
        // back to a plain tracked read there; correctness under concurrency is a
        // SQL Server concern and is exercised by the integration tests.
        if (!db.Database.IsSqlServer())
        {
            var tracked = await db.NumberSequences
                .FirstOrDefaultAsync(s => s.Name == sequenceName && s.Year == year, ct);

            if (tracked is null)
            {
                tracked = new NumberSequence { Name = sequenceName, Year = year, LastValue = 0 };
                await db.NumberSequences.AddAsync(tracked, ct);
            }

            tracked.LastValue++;
            return tracked.LastValue;
        }

        // FromSqlInterpolated parameterises sequenceName and year: they are sent as
        // SQL parameters, never concatenated into the statement text.
        var row = await db.NumberSequences
            .FromSqlInterpolated($@"
                SELECT Id, Name, Year, LastValue
                FROM NumberSequences WITH (UPDLOCK, HOLDLOCK)
                WHERE Name = {sequenceName} AND Year = {year}")
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            // First allocation for a new year.
            row = new NumberSequence { Name = sequenceName, Year = year, LastValue = 0 };
            await db.NumberSequences.AddAsync(row, ct);
        }

        row.LastValue++;
        return row.LastValue;
    }
}
