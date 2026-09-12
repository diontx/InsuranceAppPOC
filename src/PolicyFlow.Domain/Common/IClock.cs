namespace PolicyFlow.Domain.Common;

/// <summary>
/// Abstracts the system clock so time-dependent domain rules (vehicle model-year
/// surcharges, policy effective/expiration dates) are deterministic under test.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>Production clock. Always returns UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
