namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// Lifecycle of a quote. Terminal states are <see cref="Rated"/>,
/// <see cref="Referral"/>, <see cref="Declined"/> and <see cref="FollowUp"/>: once
/// a quote reaches one, the UI stops polling and the worker will not re-rate it.
/// Only a human underwriter moves a quote out of <see cref="Referral"/> or
/// <see cref="FollowUp"/>.
/// </summary>
public enum QuoteStatus
{
    /// <summary>Persisted by the API; underwriting has not started.</summary>
    Pending = 0,

    /// <summary>Claimed by the underwriting worker.</summary>
    Processing = 1,

    /// <summary>Underwriting succeeded; carrier offers are available.</summary>
    Rated = 2,

    /// <summary>Requires human underwriter review. No automated offers.</summary>
    Referral = 3,

    /// <summary>Ineligible for coverage.</summary>
    Declined = 4,

    /// <summary>
    /// An underwriter reviewed the referral and asked for more information before
    /// deciding. Still in the underwriting queue; no automated offers.
    /// </summary>
    /// <remarks>
    /// Appended rather than inserted: statuses are persisted as strings, but the
    /// numeric values are part of the public enum and must not shift.
    /// </remarks>
    FollowUp = 5
}

public static class QuoteStatusExtensions
{
    /// <summary>True when no further automated processing will change the status.</summary>
    /// <remarks>
    /// <see cref="QuoteStatus.FollowUp"/> is terminal in this sense: the worker must
    /// never re-rate it, and a polling client has nothing to wait for. Only an
    /// underwriter decision changes it.
    /// </remarks>
    public static bool IsTerminal(this QuoteStatus status) =>
        status is QuoteStatus.Rated or QuoteStatus.Referral or QuoteStatus.Declined or QuoteStatus.FollowUp;

    /// <summary>True while the quote sits in the underwriter work queue.</summary>
    public static bool IsAwaitingUnderwriter(this QuoteStatus status) =>
        status is QuoteStatus.Referral or QuoteStatus.FollowUp;
}
