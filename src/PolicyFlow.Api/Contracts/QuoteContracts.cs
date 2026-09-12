using System.ComponentModel.DataAnnotations;

namespace PolicyFlow.Api.Contracts;

/// <summary>
/// Request body for <c>POST /api/quotes</c>.
/// </summary>
/// <remarks>
/// A dedicated API contract rather than reusing the application command, so the
/// wire format can evolve independently of the internal model and OpenAPI shows
/// exactly what a client must send. The data annotations here give fast, per-field
/// 400s; FluentValidation in the application layer enforces the fuller rules; the
/// domain constructor is the final authority.
/// </remarks>
public sealed record SubmitQuoteRequest
{
    /// <summary>Full name of the applicant.</summary>
    /// <example>Alex Johnson</example>
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string CustomerName { get; init; } = string.Empty;

    /// <summary>Two-letter USPS code for the garaging state.</summary>
    /// <example>GA</example>
    [Required]
    [StringLength(2, MinimumLength = 2)]
    public string State { get; init; } = string.Empty;

    /// <summary>Vehicle model year.</summary>
    /// <example>2024</example>
    [Range(1981, 2100)]
    public int VehicleYear { get; init; }

    /// <example>Toyota</example>
    [Required]
    [StringLength(50)]
    public string VehicleMake { get; init; } = string.Empty;

    /// <example>Camry</example>
    [Required]
    [StringLength(50)]
    public string VehicleModel { get; init; } = string.Empty;

    /// <summary>Estimated miles driven per year.</summary>
    /// <example>12000</example>
    [Range(0, 100_000)]
    public int AnnualMileage { get; init; }

    /// <summary>Months of continuous prior coverage. Zero triggers a referral.</summary>
    /// <example>36</example>
    [Range(0, 600)]
    public int PriorInsuranceMonths { get; init; }
}

/// <summary>Response body for an accepted quote submission (HTTP 202).</summary>
public sealed record SubmitQuoteResponse
{
    public required Guid QuoteId { get; init; }

    /// <summary>Customer-facing quote number, e.g. <c>Q-2026-000042</c>.</summary>
    public required string QuoteNumber { get; init; }

    /// <summary>Always <c>Pending</c> at submission; underwriting is asynchronous.</summary>
    public required string Status { get; init; }

    /// <summary>Poll this until <c>isTerminal</c> is true.</summary>
    public required string StatusUrl { get; init; }

    public required string CorrelationId { get; init; }
}

/// <summary>Request body for <c>POST /api/policies/from-quote/{quoteId}</c>.</summary>
public sealed record CreatePolicyRequest
{
    /// <summary>
    /// The carrier offer the customer selected. Omit to bind the cheapest offer
    /// in the panel.
    /// </summary>
    public Guid? OfferId { get; init; }

    /// <summary>Requested effective date. Omit for tomorrow.</summary>
    public DateOnly? EffectiveDate { get; init; }
}

/// <summary>Request body for <c>POST /api/policies/{policyId}/payments</c>.</summary>
public sealed record SubmitPaymentRequest
{
    /// <summary>Amount to charge, in dollars.</summary>
    /// <example>125.00</example>
    [Range(0.01, 100_000)]
    public decimal Amount { get; init; }
}
