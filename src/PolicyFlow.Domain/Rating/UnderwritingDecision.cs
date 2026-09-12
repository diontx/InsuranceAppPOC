namespace PolicyFlow.Domain.Rating;

/// <summary>
/// Outcome of the eligibility pass that runs before any carrier is shopped.
/// Separating eligibility from pricing means a declined or referred risk never
/// reaches the carrier fan-out, which is both faster and easier to explain.
/// </summary>
public sealed record UnderwritingDecision
{
    public required UnderwritingOutcome Outcome { get; init; }

    /// <summary>
    /// Reference premium computed from the rules in POC-INSTRUCTIONS.md section 9.
    /// Carriers price independently; this is the house baseline the demo compares
    /// the panel against. Null when the risk is not eligible.
    /// </summary>
    public decimal? BaselineAnnualPremium { get; init; }

    /// <summary>Customer-facing explanation for a referral or decline.</summary>
    public string? Reason { get; init; }

    /// <summary>Ordered, auditable list of every rule that fired.</summary>
    public required IReadOnlyList<string> Trace { get; init; }

    public static UnderwritingDecision Eligible(decimal baseline, IReadOnlyList<string> trace) =>
        new() { Outcome = UnderwritingOutcome.Eligible, BaselineAnnualPremium = baseline, Trace = trace };

    public static UnderwritingDecision Refer(string reason, IReadOnlyList<string> trace) =>
        new() { Outcome = UnderwritingOutcome.Refer, Reason = reason, Trace = trace };

    public static UnderwritingDecision Decline(string reason, IReadOnlyList<string> trace) =>
        new() { Outcome = UnderwritingOutcome.Decline, Reason = reason, Trace = trace };
}

public enum UnderwritingOutcome
{
    /// <summary>Proceed to the carrier marketplace fan-out.</summary>
    Eligible = 0,

    /// <summary>Requires a human underwriter. No automated offers.</summary>
    Refer = 1,

    /// <summary>Ineligible for coverage on this program.</summary>
    Decline = 2
}
