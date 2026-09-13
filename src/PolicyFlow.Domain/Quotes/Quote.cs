using PolicyFlow.Domain.Common;

namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// The quote aggregate root. Owns its own state machine: callers may only move a
/// quote through <see cref="MarkProcessing"/>, <see cref="MarkRated"/>,
/// <see cref="MarkReferral"/> or <see cref="MarkDecline"/>, never by assigning
/// <see cref="Status"/> directly. That is what stops a redelivered Service Bus
/// message from corrupting a quote a concurrent delivery already finished.
/// </summary>
/// <remarks>
/// A referred quote leaves the automated pipeline and is resolved by a human
/// through <see cref="ApproveReferral"/>, <see cref="RequestFollowUp"/> or
/// <see cref="DeclineReferral"/>. Those are separate methods from the automated
/// transitions so the two can never be confused: the worker cannot approve a
/// referral, and an underwriter cannot rate a quote that never needed review.
/// </remarks>
public class Quote
{
    /// <summary>Longest underwriter note or decline reason the schema stores.</summary>
    public const int MaxReviewNoteLength = 500;

    /// <summary>Longest underwriter identity the schema stores.</summary>
    public const int MaxReviewerLength = 120;

    // EF Core materialises entities through this constructor.
    private Quote() { }

    private readonly List<CarrierOffer> _offers = [];

    public Guid Id { get; private set; }

    /// <summary>Human-facing identifier, e.g. Q-2026-000042. Unique.</summary>
    public string QuoteNumber { get; private set; } = null!;

    public string CustomerName { get; private set; } = null!;

    /// <summary>Two-letter USPS state code of the garaging address.</summary>
    public string State { get; private set; } = null!;

    public int VehicleYear { get; private set; }

    public string VehicleMake { get; private set; } = null!;

    public string VehicleModel { get; private set; } = null!;

    public int AnnualMileage { get; private set; }

    /// <summary>Months of continuous prior coverage. Zero forces a referral.</summary>
    public int PriorInsuranceMonths { get; private set; }

    public QuoteStatus Status { get; private set; }

    /// <summary>
    /// Premium of the cheapest offer the carrier panel returned, denormalised onto
    /// the aggregate so the status endpoint answers without a fan-in read.
    /// Null until the quote is <see cref="QuoteStatus.Rated"/>.
    /// </summary>
    public decimal? AnnualPremium { get; private set; }

    /// <summary>Carrier that produced <see cref="AnnualPremium"/>.</summary>
    public string? BestCarrierId { get; private set; }

    public CoverageTier? BestCoverageTier { get; private set; }

    /// <summary>Explains a Referral or Declined outcome to the customer.</summary>
    public string? DecisionReason { get; private set; }

    public QuoteChannel Channel { get; private set; }

    /// <summary>Traces this quote across API, SQL, Service Bus and worker logs.</summary>
    public string CorrelationId { get; private set; } = null!;

    /// <summary>Identity of the underwriter who last acted on a referral.</summary>
    public string? ReviewedBy { get; private set; }

    /// <summary>When the underwriter last acted on a referral.</summary>
    public DateTime? ReviewedUtc { get; private set; }

    /// <summary>
    /// The underwriter's note on their last decision. Internal to underwriting,
    /// unlike <see cref="DecisionReason"/>, which a customer can see.
    /// </summary>
    public string? ReviewNote { get; private set; }

    public DateTime CreatedUtc { get; private set; }

    public DateTime UpdatedUtc { get; private set; }

    /// <summary>SQL Server rowversion, used for optimistic concurrency.</summary>
    public byte[]? RowVersion { get; private set; }

    /// <summary>Priced options from the mock carrier panel, cheapest first.</summary>
    public IReadOnlyCollection<CarrierOffer> Offers => _offers.AsReadOnly();

    /// <summary>
    /// Creates a quote in <see cref="QuoteStatus.Pending"/>. No rating happens
    /// here: the API returns 202 and underwriting runs asynchronously.
    /// </summary>
    public static Quote Submit(
        string quoteNumber,
        string customerName,
        string state,
        int vehicleYear,
        string vehicleMake,
        string vehicleModel,
        int annualMileage,
        int priorInsuranceMonths,
        QuoteChannel channel,
        string correlationId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber))
        {
            throw new DomainException("Quote number is required.");
        }

        if (string.IsNullOrWhiteSpace(customerName))
        {
            throw new DomainException("Customer name is required.");
        }

        if (string.IsNullOrWhiteSpace(state) || state.Trim().Length != 2)
        {
            throw new DomainException("State must be a two-letter code.");
        }

        if (string.IsNullOrWhiteSpace(vehicleMake))
        {
            throw new DomainException("Vehicle make is required.");
        }

        if (string.IsNullOrWhiteSpace(vehicleModel))
        {
            throw new DomainException("Vehicle model is required.");
        }

        if (vehicleYear is < 1900 or > 2100)
        {
            throw new DomainException("Vehicle year is outside the supported range.");
        }

        if (annualMileage is < 0 or > 200_000)
        {
            throw new DomainException("Annual mileage is outside the supported range.");
        }

        if (priorInsuranceMonths is < 0 or > 600)
        {
            throw new DomainException("Prior insurance months is outside the supported range.");
        }

        return new Quote
        {
            Id = Guid.NewGuid(),
            QuoteNumber = quoteNumber,
            CustomerName = customerName.Trim(),
            State = state.Trim().ToUpperInvariant(),
            VehicleYear = vehicleYear,
            VehicleMake = vehicleMake.Trim(),
            VehicleModel = vehicleModel.Trim(),
            AnnualMileage = annualMileage,
            PriorInsuranceMonths = priorInsuranceMonths,
            Status = QuoteStatus.Pending,
            Channel = channel,
            CorrelationId = correlationId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>
    /// Claims the quote for underwriting. Legal only from
    /// <see cref="QuoteStatus.Pending"/>, which makes a duplicate delivery
    /// detectable rather than silently destructive.
    /// </summary>
    public void MarkProcessing(DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Processing, QuoteStatus.Pending);
        Status = QuoteStatus.Processing;
        UpdatedUtc = nowUtc;
    }

    /// <summary>
    /// Completes underwriting with the carrier panel's priced offers. The cheapest
    /// offer is promoted onto the aggregate as the headline premium.
    /// </summary>
    public void MarkRated(IEnumerable<CarrierOffer> offers, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Rated, QuoteStatus.Pending, QuoteStatus.Processing);

        var ordered = offers.OrderBy(o => o.AnnualPremium).ToList();
        if (ordered.Count == 0)
        {
            throw new DomainException(
                "A rated quote requires at least one carrier offer. Use MarkDecline when no carrier will bind.");
        }

        ApplyOffers(ordered, nowUtc);
    }

    /// <summary>Routes the quote to a human underwriter. No premium is published.</summary>
    public void MarkReferral(string reason, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Referral, QuoteStatus.Pending, QuoteStatus.Processing);
        Status = QuoteStatus.Referral;
        AnnualPremium = null;
        BestCarrierId = null;
        BestCoverageTier = null;
        DecisionReason = reason;
        UpdatedUtc = nowUtc;
    }

    /// <summary>Rejects the risk outright.</summary>
    public void MarkDecline(string reason, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Declined, QuoteStatus.Pending, QuoteStatus.Processing);
        Status = QuoteStatus.Declined;
        AnnualPremium = null;
        BestCarrierId = null;
        BestCoverageTier = null;
        DecisionReason = reason;
        UpdatedUtc = nowUtc;
    }

    // ------------------------------------------------------------------
    // Underwriter review. Legal only while the quote is in the work queue.
    // ------------------------------------------------------------------

    /// <summary>
    /// An underwriter approves a referred risk. The quote becomes
    /// <see cref="QuoteStatus.Rated"/> with the offers the carrier panel returned
    /// for it, and can then be bound like any other rated quote.
    /// </summary>
    /// <remarks>
    /// Approval does not invent a price. The underwriter overrides the house
    /// eligibility rule, but each carrier still applies its own appetite, so a risk
    /// no carrier will write cannot be approved — only followed up or declined.
    /// </remarks>
    public void ApproveReferral(IEnumerable<CarrierOffer> offers, string underwriter, string? note, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Rated, QuoteStatus.Referral, QuoteStatus.FollowUp);
        var reviewer = RequireReviewer(underwriter);
        var trimmedNote = OptionalNote(note);

        var ordered = offers.OrderBy(o => o.AnnualPremium).ToList();
        if (ordered.Count == 0)
        {
            throw new DomainException(
                $"No carrier on the panel will write quote {QuoteNumber}, so it cannot be approved. " +
                "Request follow-up or decline it.");
        }

        ApplyOffers(ordered, nowUtc);
        RecordReview(reviewer, trimmedNote, nowUtc);
    }

    /// <summary>
    /// An underwriter needs more information before deciding. The quote stays in
    /// the work queue as <see cref="QuoteStatus.FollowUp"/>, and the note becomes
    /// the customer-facing explanation of what is needed.
    /// </summary>
    /// <remarks>
    /// Allowed from <see cref="QuoteStatus.FollowUp"/> too, so an underwriter can
    /// revise the request without a decision being forced.
    /// </remarks>
    public void RequestFollowUp(string underwriter, string note, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.FollowUp, QuoteStatus.Referral, QuoteStatus.FollowUp);
        var reviewer = RequireReviewer(underwriter);
        var requiredNote = RequiredNote(note, "A follow-up request must say what information is needed.");

        Status = QuoteStatus.FollowUp;
        DecisionReason = requiredNote;
        UpdatedUtc = nowUtc;
        RecordReview(reviewer, requiredNote, nowUtc);
    }

    /// <summary>An underwriter rejects a referred risk.</summary>
    public void DeclineReferral(string underwriter, string reason, DateTime nowUtc)
    {
        RequireStatus(QuoteStatus.Declined, QuoteStatus.Referral, QuoteStatus.FollowUp);
        var reviewer = RequireReviewer(underwriter);
        var requiredReason = RequiredNote(reason, "A decline must state the reason.");

        Status = QuoteStatus.Declined;
        AnnualPremium = null;
        BestCarrierId = null;
        BestCoverageTier = null;
        DecisionReason = requiredReason;
        UpdatedUtc = nowUtc;
        RecordReview(reviewer, requiredReason, nowUtc);
    }

    /// <summary>
    /// True when underwriting has already reached a terminal decision. The worker
    /// checks this so a redelivered message becomes a no-op.
    /// </summary>
    public bool IsAlreadyUnderwritten() => Status.IsTerminal();

    /// <summary>
    /// Resolves the offer the customer selected when binding a policy. Falls back
    /// to the cheapest offer when the caller does not name one.
    /// </summary>
    public CarrierOffer SelectOfferForBinding(Guid? offerId)
    {
        if (Status != QuoteStatus.Rated)
        {
            throw new DomainException(
                $"Quote {QuoteNumber} is {Status}; only a Rated quote can be bound to a policy.");
        }

        if (offerId is null)
        {
            return _offers.OrderBy(o => o.AnnualPremium).First();
        }

        return _offers.SingleOrDefault(o => o.Id == offerId.Value)
            ?? throw new DomainException($"Offer {offerId} does not belong to quote {QuoteNumber}.");
    }

    /// <summary>Replaces the offer set and promotes the cheapest offer to the headline.</summary>
    private void ApplyOffers(List<CarrierOffer> ordered, DateTime nowUtc)
    {
        _offers.Clear();
        foreach (var offer in ordered)
        {
            offer.AttachTo(Id);
            _offers.Add(offer);
        }

        var best = ordered[0];
        Status = QuoteStatus.Rated;
        AnnualPremium = best.AnnualPremium;
        BestCarrierId = best.CarrierId;
        BestCoverageTier = best.CoverageTier;
        DecisionReason = null;
        UpdatedUtc = nowUtc;
    }

    private void RecordReview(string reviewer, string? note, DateTime nowUtc)
    {
        ReviewedBy = reviewer;
        ReviewNote = note;
        ReviewedUtc = nowUtc;
    }

    private static string RequireReviewer(string underwriter)
    {
        // An anonymous decision on a risk is not auditable, so identity is an
        // invariant of the aggregate rather than something the API remembers to set.
        if (string.IsNullOrWhiteSpace(underwriter))
        {
            throw new DomainException("An underwriter decision must record who made it.");
        }

        var trimmed = underwriter.Trim();
        if (trimmed.Length > MaxReviewerLength)
        {
            throw new DomainException($"Underwriter identity must be {MaxReviewerLength} characters or fewer.");
        }

        return trimmed;
    }

    private static string RequiredNote(string note, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException(missingMessage);
        }

        return OptionalNote(note)!;
    }

    private static string? OptionalNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var trimmed = note.Trim();
        if (trimmed.Length > MaxReviewNoteLength)
        {
            throw new DomainException($"Underwriter notes must be {MaxReviewNoteLength} characters or fewer.");
        }

        return trimmed;
    }

    private void RequireStatus(QuoteStatus target, params QuoteStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new DomainException(
                $"Cannot transition quote {QuoteNumber} from {Status} to {target}. " +
                $"Allowed source states: {string.Join(", ", allowedFrom)}.");
        }
    }
}
