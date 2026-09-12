using Microsoft.Extensions.Logging;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Quotes;

/// <summary>
/// Request in the legacy Web Forms wire format: PascalCase field names, everything
/// as a string, abbreviated names.
/// </summary>
/// <remarks>
/// This shape is not negotiable — it is what the existing Web Forms application
/// already posts. Rather than change the legacy client (a change that would need
/// its own release, regression test and deployment window), the modern service
/// accepts the old contract and translates it.
/// </remarks>
public sealed record LegacyQuoteRequest
{
    public string? InsuredName { get; init; }
    public string? RiskState { get; init; }
    public string? VehYear { get; init; }
    public string? VehMake { get; init; }
    public string? VehModel { get; init; }
    public string? Mileage { get; init; }
    public string? PriorCovMonths { get; init; }
}

/// <summary>
/// Response in the legacy format: a numeric status code and a message, the way an
/// older SOAP or ASMX service would answer.
/// </summary>
public sealed record LegacyQuoteResponse
{
    /// <summary>0 = accepted, 1 = validation failure, 2 = system error.</summary>
    public required int ReturnCode { get; init; }

    public required string ReturnMessage { get; init; }

    public string? QuoteNbr { get; init; }
    public string? QuoteGuid { get; init; }
    public string? QuoteStatus { get; init; }

    /// <summary>Where the legacy page polls for the underwriting result.</summary>
    public string? StatusUrl { get; init; }

    public string? CorrelationId { get; init; }

    public static LegacyQuoteResponse Accepted(SubmitQuoteResult result) => new()
    {
        ReturnCode = 0,
        ReturnMessage = "QUOTE ACCEPTED FOR UNDERWRITING",
        QuoteNbr = result.QuoteNumber,
        QuoteGuid = result.QuoteId.ToString(),
        QuoteStatus = result.Status.ToUpperInvariant(),
        StatusUrl = result.StatusUrl,
        CorrelationId = result.CorrelationId
    };

    public static LegacyQuoteResponse ValidationFailure(string message) => new()
    {
        ReturnCode = 1,
        ReturnMessage = message.ToUpperInvariant()
    };
}

/// <summary>
/// The modernization seam: translates the legacy contract into the modern
/// application command and the modern result back into the legacy response.
/// </summary>
/// <remarks>
/// <para>
/// This class contains <b>translation only</b>. It holds no rating rules, no
/// eligibility logic and no persistence. It calls the exact same
/// <see cref="SubmitQuoteHandler"/> the React SPA reaches through
/// <c>POST /api/quotes</c>.
/// </para>
/// <para>
/// That constraint is the whole point of the pattern. The failure mode in legacy
/// modernization is not "the old system still exists" — it is the same business
/// rule existing twice, in two languages, drifting apart until the two channels
/// quote different prices for the same risk. Every rule lives once, in the domain
/// layer. The seam is a thin, disposable translation layer whose only job is to
/// let the old client keep working until it can be retired.
/// </para>
/// <para>
/// See <c>docs/LEGACY-MODERNIZATION.md</c> for the incremental migration strategy
/// this seam is part of.
/// </para>
/// </remarks>
public sealed class LegacyQuoteAdapter(
    SubmitQuoteHandler submitQuote,
    ILogger<LegacyQuoteAdapter> logger)
{
    public async Task<LegacyQuoteResponse> SubmitAsync(LegacyQuoteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // --- Legacy-format validation. The old client sends everything as a string,
        //     so type coercion happens here rather than leaking string parsing into
        //     the application layer.
        if (string.IsNullOrWhiteSpace(request.InsuredName))
        {
            return LegacyQuoteResponse.ValidationFailure("INSURED NAME IS REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(request.RiskState) || request.RiskState.Trim().Length != 2)
        {
            return LegacyQuoteResponse.ValidationFailure("RISK STATE MUST BE A 2 CHARACTER CODE");
        }

        if (!int.TryParse(request.VehYear, out var vehicleYear))
        {
            return LegacyQuoteResponse.ValidationFailure("VEH YEAR IS NOT NUMERIC");
        }

        if (string.IsNullOrWhiteSpace(request.VehMake))
        {
            return LegacyQuoteResponse.ValidationFailure("VEH MAKE IS REQUIRED");
        }

        if (string.IsNullOrWhiteSpace(request.VehModel))
        {
            return LegacyQuoteResponse.ValidationFailure("VEH MODEL IS REQUIRED");
        }

        // Legacy pages format numbers with thousands separators, e.g. "12,000".
        if (!int.TryParse(
                (request.Mileage ?? string.Empty).Replace(",", string.Empty),
                out var mileage))
        {
            return LegacyQuoteResponse.ValidationFailure("MILEAGE IS NOT NUMERIC");
        }

        if (!int.TryParse(request.PriorCovMonths, out var priorCovMonths))
        {
            return LegacyQuoteResponse.ValidationFailure("PRIOR COV MONTHS IS NOT NUMERIC");
        }

        logger.LogInformation(
            "Legacy quote request translated. Operation={Operation} Channel={Channel} " +
            "RiskState={RiskState} Result={Result}",
            "LegacyQuoteAdapter", nameof(QuoteChannel.LegacyWebForms), request.RiskState, "Translated");

        try
        {
            // --- Same handler the modern API uses. No duplicated business logic.
            var result = await submitQuote.HandleAsync(
                new SubmitQuoteCommand
                {
                    CustomerName = request.InsuredName.Trim(),
                    State = request.RiskState.Trim(),
                    VehicleYear = vehicleYear,
                    VehicleMake = request.VehMake.Trim(),
                    VehicleModel = request.VehModel.Trim(),
                    AnnualMileage = mileage,
                    PriorInsuranceMonths = priorCovMonths,
                    Channel = QuoteChannel.LegacyWebForms
                },
                ct);

            return LegacyQuoteResponse.Accepted(result);
        }
        catch (DomainException ex)
        {
            // Translate a modern domain error back into the legacy return-code
            // convention. The legacy client cannot interpret ProblemDetails.
            logger.LogWarning(
                "Legacy quote rejected by domain rules. Operation={Operation} Reason={Reason} Result={Result}",
                "LegacyQuoteAdapter", ex.Message, "Rejected");

            return LegacyQuoteResponse.ValidationFailure(ex.Message);
        }
    }
}
