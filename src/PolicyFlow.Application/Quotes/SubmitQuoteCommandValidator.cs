using FluentValidation;

namespace PolicyFlow.Application.Quotes;

/// <summary>
/// Input validation for a quote submission.
/// </summary>
/// <remarks>
/// <para>
/// This is the first of two layers. It rejects malformed input early with a helpful
/// 400 and a per-field error list. The domain invariants in <c>Quote.Submit</c> are
/// the second layer and the authoritative one: they hold no matter which entry
/// point the data arrived through, including the legacy adapter and the worker.
/// </para>
/// <para>
/// Validation is also a security control. Bounded lengths and character sets keep
/// oversized or hostile payloads out of the persistence layer. SQL injection is
/// separately impossible here because EF Core parameterises every query — there is
/// no string-concatenated SQL anywhere in this solution.
/// </para>
/// </remarks>
public sealed class SubmitQuoteCommandValidator : AbstractValidator<SubmitQuoteCommand>
{
    /// <summary>States the program is filed in. Anything else is rejected outright.</summary>
    private static readonly string[] SupportedStates =
        ["GA", "FL", "TX", "NC", "SC", "TN", "AL", "VA", "MS"];

    public SubmitQuoteCommandValidator()
    {
        RuleFor(x => x.CustomerName)
            .NotEmpty().WithMessage("Customer name is required.")
            .MaximumLength(120).WithMessage("Customer name must be 120 characters or fewer.")
            // Letters, spaces, hyphens, apostrophes and periods cover real names
            // (O'Brien, Smith-Jones, Jr.) while excluding markup and control characters.
            // FAIL-DEPENDENCY and FAIL-CARRIER pass because uppercase and hyphens are allowed.
            .Matches(@"^[\p{L}\p{M}\s'\-\.]+$")
            .WithMessage("Customer name may contain only letters, spaces, hyphens, apostrophes and periods.");

        RuleFor(x => x.State)
            .NotEmpty().WithMessage("State is required.")
            .Length(2).WithMessage("State must be a two-letter code.")
            .Must(s => SupportedStates.Contains(s.ToUpperInvariant()))
            .WithMessage($"State must be one of: {string.Join(", ", SupportedStates)}.");

        RuleFor(x => x.VehicleYear)
            .InclusiveBetween(1981, DateTime.UtcNow.Year + 1)
            .WithMessage($"Vehicle year must be between 1981 and {DateTime.UtcNow.Year + 1}.");

        RuleFor(x => x.VehicleMake)
            .NotEmpty().WithMessage("Vehicle make is required.")
            .MaximumLength(50)
            .Matches(@"^[\p{L}\p{N}\s'\-\.]+$")
            .WithMessage("Vehicle make contains unsupported characters.");

        RuleFor(x => x.VehicleModel)
            .NotEmpty().WithMessage("Vehicle model is required.")
            .MaximumLength(50)
            .Matches(@"^[\p{L}\p{N}\s'\-\.]+$")
            .WithMessage("Vehicle model contains unsupported characters.");

        RuleFor(x => x.AnnualMileage)
            .InclusiveBetween(0, 100_000)
            .WithMessage("Annual mileage must be between 0 and 100,000.");

        RuleFor(x => x.PriorInsuranceMonths)
            .InclusiveBetween(0, 600)
            .WithMessage("Prior insurance months must be between 0 and 600.");
    }
}
