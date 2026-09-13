using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Underwriting;

/// <summary>The underwriter work queue.</summary>
public sealed record UnderwritingQueueView
{
    /// <summary>Quotes awaiting an underwriter, oldest submission first.</summary>
    public required IReadOnlyList<UnderwritingQueueItem> Items { get; init; }

    public required int ReferralCount { get; init; }

    public required int FollowUpCount { get; init; }

    /// <summary>
    /// True when the queue holds more quotes than one read returns. The UI says so
    /// rather than implying the list is complete.
    /// </summary>
    public required bool IsTruncated { get; init; }

    public required DateTime GeneratedUtc { get; init; }
}

/// <summary>One row in the work queue. Deliberately narrow: enough to triage.</summary>
public sealed record UnderwritingQueueItem
{
    public required Guid QuoteId { get; init; }
    public required string QuoteNumber { get; init; }
    public required string CustomerName { get; init; }
    public required string State { get; init; }
    public required string VehicleDescription { get; init; }

    /// <summary><c>Referral</c> or <c>FollowUp</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Why the quote is in the queue, or what the follow-up asked for.</summary>
    public string? Reason { get; init; }

    public required string Channel { get; init; }
    public required DateTime SubmittedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }
    public string? ReviewedBy { get; init; }

    public static UnderwritingQueueItem From(Quote quote) => new()
    {
        QuoteId = quote.Id,
        QuoteNumber = quote.QuoteNumber,
        CustomerName = quote.CustomerName,
        State = quote.State,
        VehicleDescription = $"{quote.VehicleYear} {quote.VehicleMake} {quote.VehicleModel}",
        Status = quote.Status.ToString(),
        Reason = quote.DecisionReason,
        Channel = quote.Channel.ToString(),
        SubmittedUtc = quote.CreatedUtc,
        UpdatedUtc = quote.UpdatedUtc,
        ReviewedBy = quote.ReviewedBy
    };
}

/// <summary>
/// Everything an underwriter needs to decide on one application.
/// </summary>
/// <remarks>
/// The eligibility trace and carrier appetite are <b>computed by the domain
/// services</b>, not re-implemented for this screen. The review page therefore
/// shows exactly the rules the worker applied, and the carriers that would price
/// the risk on approval, without the UI knowing a single rule.
/// </remarks>
public sealed record UnderwritingReviewView
{
    public required Guid QuoteId { get; init; }
    public required string QuoteNumber { get; init; }
    public required string Status { get; init; }

    /// <summary>True while a decision can still be recorded.</summary>
    public required bool IsAwaitingReview { get; init; }

    // --- The application, as the customer submitted it.
    public required string CustomerName { get; init; }
    public required string State { get; init; }
    public required int VehicleYear { get; init; }
    public required string VehicleMake { get; init; }
    public required string VehicleModel { get; init; }
    public required int AnnualMileage { get; init; }
    public required int PriorInsuranceMonths { get; init; }
    public required string Channel { get; init; }
    public required string CorrelationId { get; init; }
    public required DateTime SubmittedUtc { get; init; }
    public required DateTime UpdatedUtc { get; init; }

    /// <summary>Referral reason, follow-up request, or decline reason.</summary>
    public string? DecisionReason { get; init; }

    // --- Why the automated pipeline referred it.

    /// <summary><c>Eligible</c>, <c>Refer</c> or <c>Decline</c>, from the house rules.</summary>
    public required string EligibilityOutcome { get; init; }

    /// <summary>House baseline premium. Null when the house rules did not price it.</summary>
    public decimal? BaselineAnnualPremium { get; init; }

    /// <summary>Every eligibility and pricing rule that fired, in order.</summary>
    public required IReadOnlyList<string> EligibilityTrace { get; init; }

    /// <summary>Which carriers would price this risk if it were approved, and why not.</summary>
    public required IReadOnlyList<CarrierAppetiteView> CarrierAppetite { get; init; }

    // --- The underwriter's last action, and its result.
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedUtc { get; init; }
    public string? ReviewNote { get; init; }
    public decimal? AnnualPremium { get; init; }
    public string? BestCarrierId { get; init; }
    public required int OfferCount { get; init; }
}

/// <summary>One carrier's appetite for the risk under review.</summary>
public sealed record CarrierAppetiteView
{
    public required string CarrierId { get; init; }
    public required string CarrierName { get; init; }
    public required bool WillWrite { get; init; }

    /// <summary>Why the carrier will not write it. Null when it will.</summary>
    public string? Reason { get; init; }
}
