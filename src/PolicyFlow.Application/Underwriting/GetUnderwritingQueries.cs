using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Application.Underwriting;

/// <summary>Reads the underwriter work queue.</summary>
public sealed class GetUnderwritingQueueHandler(IQuoteRepository quotes, IClock clock)
{
    /// <summary>Most quotes one queue read returns.</summary>
    public const int MaxQueueSize = 200;

    public async Task<UnderwritingQueueView> HandleAsync(CancellationToken ct = default)
    {
        // One extra row answers "is there more?" without a second COUNT query.
        var awaiting = await quotes.GetAwaitingUnderwriterAsync(MaxQueueSize + 1, ct);
        var page = awaiting.Take(MaxQueueSize).ToList();

        return new UnderwritingQueueView
        {
            Items = page.Select(UnderwritingQueueItem.From).ToList(),
            ReferralCount = page.Count(q => q.Status == QuoteStatus.Referral),
            FollowUpCount = page.Count(q => q.Status == QuoteStatus.FollowUp),
            IsTruncated = awaiting.Count > MaxQueueSize,
            GeneratedUtc = clock.UtcNow
        };
    }
}

/// <summary>Builds the review view for one quote.</summary>
public sealed class GetUnderwritingReviewHandler(
    IQuoteRepository quotes,
    ICarrierCatalog catalog,
    QuoteRatingService quoteRating,
    CarrierRatingService carrierRating)
{
    public async Task<UnderwritingReviewView?> HandleAsync(Guid quoteId, CancellationToken ct = default)
    {
        var quote = await quotes.GetWithOffersAsync(quoteId, ct);
        return quote is null ? null : await BuildAsync(quote, ct);
    }

    /// <summary>Projects an already-loaded quote. Used after a decision is saved.</summary>
    public async Task<UnderwritingReviewView> BuildAsync(Quote quote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        // Evaluated as of submission, not now: the model-year rule depends on the
        // year, and the underwriter needs to see the decision the pipeline actually
        // made — not what it would decide if the application arrived today.
        var eligibility = quoteRating.Evaluate(quote, quote.CreatedUtc);

        var carriers = await catalog.GetActiveCarriersAsync(ct);
        var appetite = carriers
            .Select(carrier =>
            {
                var willWrite = carrierRating.IsInAppetite(quote, carrier, out var reason);
                return new CarrierAppetiteView
                {
                    CarrierId = carrier.CarrierId,
                    CarrierName = carrier.DisplayName,
                    WillWrite = willWrite,
                    Reason = reason
                };
            })
            .ToList();

        return new UnderwritingReviewView
        {
            QuoteId = quote.Id,
            QuoteNumber = quote.QuoteNumber,
            Status = quote.Status.ToString(),
            IsAwaitingReview = quote.Status.IsAwaitingUnderwriter(),
            CustomerName = quote.CustomerName,
            State = quote.State,
            VehicleYear = quote.VehicleYear,
            VehicleMake = quote.VehicleMake,
            VehicleModel = quote.VehicleModel,
            AnnualMileage = quote.AnnualMileage,
            PriorInsuranceMonths = quote.PriorInsuranceMonths,
            Channel = quote.Channel.ToString(),
            CorrelationId = quote.CorrelationId,
            SubmittedUtc = quote.CreatedUtc,
            UpdatedUtc = quote.UpdatedUtc,
            DecisionReason = quote.DecisionReason,
            EligibilityOutcome = eligibility.Outcome.ToString(),
            BaselineAnnualPremium = eligibility.BaselineAnnualPremium,
            EligibilityTrace = eligibility.Trace,
            CarrierAppetite = appetite,
            ReviewedBy = quote.ReviewedBy,
            ReviewedUtc = quote.ReviewedUtc,
            ReviewNote = quote.ReviewNote,
            AnnualPremium = quote.AnnualPremium,
            BestCarrierId = quote.BestCarrierId,
            OfferCount = quote.Offers.Count
        };
    }
}
