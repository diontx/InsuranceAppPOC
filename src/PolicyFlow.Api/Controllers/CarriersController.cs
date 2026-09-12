using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Api.Controllers;

/// <summary>
/// Read-only view of the carrier panel, served from the NoSQL catalog.
/// </summary>
/// <remarks>
/// Lets the SPA render carrier branding and coverage tiers before a quote has been
/// rated, so the "who will you shop me against?" panel on the landing page is real
/// data rather than hard-coded markup.
/// </remarks>
[ApiController]
[Route("api/carriers")]
[Produces("application/json")]
public sealed class CarriersController(ICarrierCatalog catalog) : ControllerBase
{
    /// <summary>Lists the active carriers on the panel.</summary>
    /// <remarks>
    /// Anonymous: this is public marketing information, the same thing a carrier
    /// panel would be printed on a brochure. No customer or pricing data is
    /// included — only configuration factors, which are what the comparison screen
    /// already discloses in its rating trace.
    /// </remarks>
    /// <response code="200">The carrier panel.</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<CarrierSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var carriers = await catalog.GetActiveCarriersAsync(ct);

        return Ok(carriers.Select(c => new CarrierSummary
        {
            CarrierId = c.CarrierId,
            DisplayName = c.DisplayName,
            Tagline = c.Tagline,
            BrandColor = c.BrandColor,
            FinancialStrengthRating = c.FinancialStrengthRating,
            StatesWritten = c.StateFactors.Keys.OrderBy(s => s).ToArray(),
            MinimumPriorInsuranceMonths = c.MinimumPriorInsuranceMonths,
            MaximumAnnualMileage = c.MaximumAnnualMileage,
            CoverageTiers = c.Coverages
                .OrderBy(kv => kv.Key)
                .Select(kv => new CoverageTierSummary
                {
                    Tier = kv.Key.ToString(),
                    Deductible = kv.Value.Deductible,
                    LimitsSummary = kv.Value.LimitsSummary,
                    BodilyInjuryPerPerson = kv.Value.BodilyInjuryPerPerson,
                    BodilyInjuryPerAccident = kv.Value.BodilyInjuryPerAccident,
                    PropertyDamage = kv.Value.PropertyDamage,
                    IncludesRoadside = kv.Value.IncludesRoadside,
                    IncludesRentalReimbursement = kv.Value.IncludesRentalReimbursement
                })
                .ToArray(),
            AvailableDiscounts = c.Discounts.Select(d => d.Name).ToArray()
        }).ToList());
    }

    /// <summary>Reads one carrier's panel entry.</summary>
    /// <response code="200">Carrier detail.</response>
    /// <response code="404">No such carrier.</response>
    [HttpGet("{carrierId}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CarrierSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string carrierId, CancellationToken ct)
    {
        var carrier = await catalog.GetCarrierAsync(carrierId, ct);

        if (carrier is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Carrier not found",
                Detail = $"No active carrier with id '{carrierId}'."
            });
        }

        return Ok(new CarrierSummary
        {
            CarrierId = carrier.CarrierId,
            DisplayName = carrier.DisplayName,
            Tagline = carrier.Tagline,
            BrandColor = carrier.BrandColor,
            FinancialStrengthRating = carrier.FinancialStrengthRating,
            StatesWritten = carrier.StateFactors.Keys.OrderBy(s => s).ToArray(),
            MinimumPriorInsuranceMonths = carrier.MinimumPriorInsuranceMonths,
            MaximumAnnualMileage = carrier.MaximumAnnualMileage,
            CoverageTiers = carrier.Coverages
                .OrderBy(kv => kv.Key)
                .Select(kv => new CoverageTierSummary
                {
                    Tier = kv.Key.ToString(),
                    Deductible = kv.Value.Deductible,
                    LimitsSummary = kv.Value.LimitsSummary,
                    BodilyInjuryPerPerson = kv.Value.BodilyInjuryPerPerson,
                    BodilyInjuryPerAccident = kv.Value.BodilyInjuryPerAccident,
                    PropertyDamage = kv.Value.PropertyDamage,
                    IncludesRoadside = kv.Value.IncludesRoadside,
                    IncludesRentalReimbursement = kv.Value.IncludesRentalReimbursement
                })
                .ToArray(),
            AvailableDiscounts = carrier.Discounts.Select(d => d.Name).ToArray()
        });
    }
}

/// <summary>
/// Public view of a carrier. Deliberately omits the rating factors and simulated
/// latency bands — those are internal configuration, not customer information.
/// </summary>
public sealed record CarrierSummary
{
    public required string CarrierId { get; init; }
    public required string DisplayName { get; init; }
    public required string Tagline { get; init; }
    public required string BrandColor { get; init; }
    public required string FinancialStrengthRating { get; init; }
    public required string[] StatesWritten { get; init; }
    public required int MinimumPriorInsuranceMonths { get; init; }
    public required int MaximumAnnualMileage { get; init; }
    public required CoverageTierSummary[] CoverageTiers { get; init; }
    public required string[] AvailableDiscounts { get; init; }
}

public sealed record CoverageTierSummary
{
    /// <summary>One of <see cref="CoverageTier"/>.</summary>
    public required string Tier { get; init; }

    public required decimal Deductible { get; init; }

    /// <summary>Limits in the conventional shorthand, e.g. <c>50/100/50</c>.</summary>
    public required string LimitsSummary { get; init; }

    public required int BodilyInjuryPerPerson { get; init; }
    public required int BodilyInjuryPerAccident { get; init; }
    public required int PropertyDamage { get; init; }
    public required bool IncludesRoadside { get; init; }
    public required bool IncludesRentalReimbursement { get; init; }
}
