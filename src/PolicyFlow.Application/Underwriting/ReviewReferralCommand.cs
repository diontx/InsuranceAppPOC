using System.Diagnostics;
using FluentValidation;
using Microsoft.Extensions.Logging;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Policies;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Underwriting;

/// <summary>The three outcomes an underwriter can record on a referral.</summary>
public enum ReviewDecision
{
    /// <summary>Accept the risk. The carrier panel prices it and the quote becomes Rated.</summary>
    Approve = 0,

    /// <summary>Ask for more information. The quote stays in the queue.</summary>
    FollowUp = 1,

    /// <summary>Reject the risk.</summary>
    Decline = 2
}

/// <summary>An underwriter's decision on one referred quote.</summary>
public sealed record ReviewReferralCommand
{
    public required Guid QuoteId { get; init; }

    public required ReviewDecision Decision { get; init; }

    /// <summary>
    /// Who decided. Set by the API from the authenticated principal, never from the
    /// request body: a client-supplied identity on an underwriting decision would
    /// make the audit fields worthless.
    /// </summary>
    public required string Underwriter { get; init; }

    /// <summary>Required for follow-up and decline; optional for approval.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Input validation for an underwriting decision. The <see cref="Quote"/> aggregate
/// enforces the same invariants authoritatively; this layer exists to return a
/// per-field 400 before any carrier is called.
/// </summary>
public sealed class ReviewReferralCommandValidator : AbstractValidator<ReviewReferralCommand>
{
    /// <summary>
    /// Shortest note accepted on a follow-up or decline. "ok" or "no" is not a
    /// reason anyone can act on or audit later. A UX rule, not a domain one, so it
    /// lives here and not in the aggregate.
    /// </summary>
    public const int MinimumNoteLength = 10;

    public ReviewReferralCommandValidator()
    {
        RuleFor(x => x.QuoteId).NotEmpty();

        RuleFor(x => x.Decision)
            .IsInEnum().WithMessage("Decision must be Approve, FollowUp or Decline.");

        RuleFor(x => x.Underwriter)
            .NotEmpty().WithMessage("An underwriter decision must record who made it.")
            .MaximumLength(Quote.MaxReviewerLength);

        RuleFor(x => x.Note)
            .MaximumLength(Quote.MaxReviewNoteLength)
            .WithMessage($"Note must be {Quote.MaxReviewNoteLength} characters or fewer.");

        When(x => x.Decision is ReviewDecision.FollowUp or ReviewDecision.Decline, () =>
        {
            RuleFor(x => x.Note)
                .Must(note => !string.IsNullOrWhiteSpace(note))
                .WithMessage(x => x.Decision == ReviewDecision.FollowUp
                    ? "Say what information is needed from the applicant."
                    : "State the reason for declining.");

            // A separate rule, not a chained .Must(): FluentValidation applies a
            // trailing .When() to EVERY validator in the chain by default, which
            // silently switched off the required check above for blank notes.
            RuleFor(x => x.Note)
                .Must(note => note!.Trim().Length >= MinimumNoteLength)
                .When(x => !string.IsNullOrWhiteSpace(x.Note))
                .WithMessage($"Note must be at least {MinimumNoteLength} characters.");
        });
    }
}

/// <summary>
/// Records an underwriter's decision on a referral.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approval shops the carrier panel synchronously.</b> The customer-facing API
/// never does that (it returns 202 and lets the worker call carriers). The trade-off
/// is different here: the caller is an internal user who has just pressed Approve
/// and needs to know whether any carrier will write the risk, the fan-out is
/// concurrent (roughly the slowest carrier, well under a second), and the gateway
/// calls happen <i>before</i> anything is written, so a slow carrier never holds a
/// database transaction open.
/// </para>
/// <para>
/// <b>Two underwriters, one quote.</b> The status pre-check turns the common case —
/// the quote was decided while this one had it open — into a clear 409. The
/// <c>rowversion</c> on <c>Quotes</c> is the actual guarantee: if both pass the
/// pre-check, the second save fails with a concurrency conflict, also a 409.
/// </para>
/// </remarks>
public sealed class ReviewReferralHandler(
    IQuoteRepository quotes,
    IUnitOfWork unitOfWork,
    CarrierMarketplace marketplace,
    GetUnderwritingReviewHandler reviews,
    IClock clock,
    ILogger<ReviewReferralHandler> logger)
{
    public async Task<UnderwritingReviewView> HandleAsync(ReviewReferralCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var stopwatch = Stopwatch.StartNew();

        var quote = await quotes.GetWithOffersAsync(command.QuoteId, ct)
            ?? throw new QuoteNotFoundException(command.QuoteId);

        if (!quote.Status.IsAwaitingUnderwriter())
        {
            logger.LogWarning(
                "Underwriting decision rejected: quote not in the queue. Operation={Operation} QuoteId={QuoteId} " +
                "QuoteNumber={QuoteNumber} Status={Status} Decision={Decision} Underwriter={Underwriter} " +
                "CorrelationId={CorrelationId} Result={Result}",
                "ReviewReferral", quote.Id, quote.QuoteNumber, quote.Status, command.Decision,
                command.Underwriter, quote.CorrelationId, "Conflict");

            throw new BusinessConflictException(
                $"Quote {quote.QuoteNumber} is {quote.Status} and is no longer awaiting underwriter review. " +
                "Refresh the queue.");
        }

        CarrierPanelResult? panel = null;

        switch (command.Decision)
        {
            case ReviewDecision.Approve:
                // Shop first, write second: no transaction is open during carrier latency.
                panel = await marketplace.ShopAsync(quote, ct);
                // Throws DomainException (422) when no carrier will write the risk.
                quote.ApproveReferral(panel.Offers, command.Underwriter, command.Note, clock.UtcNow);
                break;

            case ReviewDecision.FollowUp:
                quote.RequestFollowUp(command.Underwriter, command.Note ?? string.Empty, clock.UtcNow);
                break;

            case ReviewDecision.Decline:
                quote.DeclineReferral(command.Underwriter, command.Note ?? string.Empty, clock.UtcNow);
                break;

            default:
                throw new DomainException($"Unsupported underwriting decision '{command.Decision}'.");
        }

        await unitOfWork.SaveChangesAsync(ct);

        if (panel is not null)
        {
            // The referral left a zero-offer comparison document behind. Without this
            // refresh the customer's comparison screen would keep serving it.
            await marketplace.WriteComparisonDocumentAsync(
                quote, baselineAnnualPremium: null, quote.Offers.ToList(), panel.Declinations,
                stopwatch.ElapsedMilliseconds, ct);
        }

        logger.LogInformation(
            "Underwriting decision recorded. Operation={Operation} QuoteId={QuoteId} QuoteNumber={QuoteNumber} " +
            "Decision={Decision} Status={Status} Underwriter={Underwriter} OfferCount={OfferCount} " +
            "BestPremium={BestPremium} CorrelationId={CorrelationId} ElapsedMs={ElapsedMs} Result={Result}",
            "ReviewReferral", quote.Id, quote.QuoteNumber, command.Decision, quote.Status,
            quote.ReviewedBy, quote.Offers.Count, quote.AnnualPremium, quote.CorrelationId,
            stopwatch.ElapsedMilliseconds, "Recorded");

        return await reviews.BuildAsync(quote, ct);
    }
}
