namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// Lifecycle of a quote. Terminal states are <see cref="Rated"/>,
/// <see cref="Referral"/> and <see cref="Declined"/>: once a quote reaches one,
/// the UI stops polling and the worker will not re-rate it.
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
    Declined = 4
}

public static class QuoteStatusExtensions
{
    /// <summary>True when no further automated processing will change the status.</summary>
    public static bool IsTerminal(this QuoteStatus status) =>
        status is QuoteStatus.Rated or QuoteStatus.Referral or QuoteStatus.Declined;
}
