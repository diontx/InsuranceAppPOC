namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// Records which client submitted the quote. This is the observability hook that
/// lets the demo prove the legacy Web Forms adapter and the modern React SPA
/// travel through the identical application and domain code paths.
/// </summary>
public enum QuoteChannel
{
    /// <summary>Modern REST client (React SPA, mobile web, integration tests).</summary>
    Modern = 0,

    /// <summary>Legacy ASP.NET Web Forms application via the compatibility seam.</summary>
    LegacyWebForms = 1
}
