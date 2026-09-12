namespace PolicyFlow.Domain.Quotes;

/// <summary>
/// The three coverage packages every mock carrier offers. Each tier maps to a
/// concrete set of limits and a deductible defined in the carrier catalog, and
/// to a rating multiplier applied to the carrier's base premium.
/// </summary>
public enum CoverageTier
{
    /// <summary>State-minimum liability. No physical damage coverage.</summary>
    Basic = 0,

    /// <summary>Liability above state minimum plus comprehensive/collision.</summary>
    Standard = 1,

    /// <summary>High limits, low deductible, rental and roadside included.</summary>
    Premium = 2
}
