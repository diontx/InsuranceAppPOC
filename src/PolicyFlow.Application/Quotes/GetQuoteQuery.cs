using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Quotes;

/// <summary>Status view of a quote. Shape the React client polls.</summary>
public sealed record QuoteStatusView
{
    public required Guid QuoteId { get; init; }
    public required string QuoteNumber { get; init; }
    public required string Status { get; init; }
    public required bool IsTerminal { get; init; }
    public required string CustomerName { get; init; }
    public required string State { get; init; }
    public required string VehicleDescription { get; init; }
    public required int AnnualMileage { get; init; }
    public required int PriorInsuranceMonths { get; init; }

    /// <summary>Best available annual premium. Null unless Rated.</summary>
    public decimal? AnnualPremium { get; init; }

    public string? BestCarrierId { get; init; }
    public string? BestCoverageTier { get; init; }

    /// <summary>Explanation for a Referral or Declined outcome.</summary>
    public string? DecisionReason { get; init; }

    public required int OfferCount { get; init; }
    public required string Channel { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }

    /// <summary>Where the comparison payload lives, once offers exist.</summary>
    public string? OffersUrl { get; init; }

    public static QuoteStatusView From(Quote quote) => new()
    {
        QuoteId = quote.Id,
        QuoteNumber = quote.QuoteNumber,
        Status = quote.Status.ToString(),
        IsTerminal = quote.Status.IsTerminal(),
        CustomerName = quote.CustomerName,
        State = quote.State,
        VehicleDescription = $"{quote.VehicleYear} {quote.VehicleMake} {quote.VehicleModel}",
        AnnualMileage = quote.AnnualMileage,
        PriorInsuranceMonths = quote.PriorInsuranceMonths,
        AnnualPremium = quote.AnnualPremium,
        BestCarrierId = quote.BestCarrierId,
        BestCoverageTier = quote.BestCoverageTier?.ToString(),
        DecisionReason = quote.DecisionReason,
        OfferCount = quote.Offers.Count,
        Channel = quote.Channel.ToString(),
        CorrelationId = quote.CorrelationId,
        CreatedUtc = quote.CreatedUtc,
        UpdatedUtc = quote.UpdatedUtc,
        OffersUrl = quote.Status == QuoteStatus.Rated ? $"/api/quotes/{quote.Id}/offers" : null
    };
}

/// <summary>
/// Reads quote status. This is the endpoint the SPA polls every 1.5 seconds while
/// underwriting runs, so it is kept to a single indexed primary-key read.
/// </summary>
public sealed class GetQuoteHandler(IQuoteRepository quotes)
{
    /// <summary>Most quotes one agent's list returns.</summary>
    public const int MaxAgentQuotes = 100;

    /// <summary>The quotes an agent submitted, newest first, with their current status.</summary>
    /// <remarks>
    /// The same <see cref="QuoteStatusView"/> the single-quote endpoint returns, so a
    /// row in the agent's list and the quote's own status page cannot disagree.
    /// </remarks>
    public async Task<IReadOnlyList<QuoteStatusView>> ListSubmittedByAsync(
        string submittedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(submittedBy))
        {
            return [];
        }

        var submitted = await quotes.GetSubmittedByAsync(submittedBy.Trim(), MaxAgentQuotes, ct);
        return submitted.Select(QuoteStatusView.From).ToList();
    }

    public async Task<QuoteStatusView?> HandleAsync(Guid quoteId, CancellationToken ct = default)
    {
        var quote = await quotes.GetWithOffersAsync(quoteId, ct);
        return quote is null ? null : QuoteStatusView.From(quote);
    }

    /// <summary>
    /// Reads status by customer-facing quote number.
    /// </summary>
    /// <remarks>
    /// Exists for the legacy channel, whose own database stored only the printed
    /// quote number and never the GUID. Preserving this access path is what lets
    /// the old page poll unchanged. Backed by the unique index on QuoteNumber.
    /// </remarks>
    public async Task<QuoteStatusView?> LookupByQuoteNumberAsync(
        string quoteNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber))
        {
            return null;
        }

        var quote = await quotes.GetByQuoteNumberAsync(quoteNumber.Trim(), ct);
        return quote is null ? null : QuoteStatusView.From(quote);
    }

    /// <summary>
    /// Mobile self-service lookup by quote number and surname.
    /// </summary>
    /// <remarks>
    /// Two factors are required rather than one. A quote number alone is guessable
    /// (it is sequential), so the surname acts as a shared secret. This is adequate
    /// for a POC; production would gate this behind customer authentication, and
    /// <c>docs/SECURITY.md</c> records that decision.
    /// </remarks>
    public async Task<QuoteStatusView?> LookupAsync(string quoteNumber, string lastName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber) || string.IsNullOrWhiteSpace(lastName))
        {
            return null;
        }

        var quote = await quotes.FindForSelfServiceLookupAsync(quoteNumber.Trim(), lastName.Trim(), ct);
        return quote is null ? null : QuoteStatusView.From(quote);
    }
}
