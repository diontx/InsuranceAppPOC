using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Api.Security;
using PolicyFlow.Application.Quotes;

namespace PolicyFlow.Api.Controllers;

/// <summary>
/// Compatibility endpoints for the legacy ASP.NET Web Forms application.
/// </summary>
/// <remarks>
/// <para>
/// This is the modernization seam. The legacy application keeps posting the
/// contract it always posted — PascalCase fields, every value a string, a numeric
/// return code in the response — and this controller translates it into the modern
/// application command.
/// </para>
/// <para>
/// <b>It contains no business logic.</b> It calls
/// <see cref="LegacyQuoteAdapter"/>, which calls the same
/// <c>SubmitQuoteHandler</c> that <c>POST /api/quotes</c> uses. Rating, eligibility
/// and persistence exist exactly once. The only thing that differs between the two
/// channels is the recorded <c>Channel</c> value, which exists so the demo can
/// prove both paths converge.
/// </para>
/// <para>
/// Routed under <c>/legacy</c> rather than <c>/api</c> so it is obvious in logs,
/// metrics and the routing table which traffic is still arriving from the old
/// system — which is how you know when it is safe to retire it.
/// </para>
/// <para>See <c>docs/LEGACY-MODERNIZATION.md</c>.</para>
/// </remarks>
[ApiController]
[Route("legacy")]
[Produces("application/json")]
public sealed class LegacyController(
    LegacyQuoteAdapter adapter,
    GetQuoteHandler getQuote,
    ILogger<LegacyController> logger) : ControllerBase
{
    /// <summary>Accepts a quote in the legacy Web Forms wire format.</summary>
    /// <remarks>
    /// Always returns HTTP 200 with a <c>ReturnCode</c> in the body: 0 accepted,
    /// 1 validation failure, 2 system error. The legacy client predates
    /// <c>ProblemDetails</c> and cannot interpret a 4xx body, so the old convention
    /// is preserved rather than forcing a change to a system we are trying not to
    /// touch.
    /// </remarks>
    /// <response code="200">Always. Inspect <c>ReturnCode</c> for the outcome.</response>
    [HttpPost("quotes")]
    [Authorize(Policy = PolicyFlowPolicies.LegacyIntegration)]
    [ProducesResponseType(typeof(LegacyQuoteResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitQuote(
        [FromBody] LegacyQuoteRequest request,
        CancellationToken ct)
    {
        var response = await adapter.SubmitAsync(request, ct);

        logger.LogInformation(
            "Legacy quote endpoint responded. Operation={Operation} ReturnCode={ReturnCode} " +
            "QuoteNbr={QuoteNbr} Result={Result}",
            "LegacySubmitQuote", response.ReturnCode, response.QuoteNbr,
            response.ReturnCode == 0 ? "Accepted" : "Rejected");

        // 200 with a return code, not a 4xx. This is a deliberate concession to the
        // legacy client, documented so nobody "fixes" it into a 400 later.
        return Ok(response);
    }

    /// <summary>
    /// Status lookup by quote number in the legacy field naming.
    /// </summary>
    /// <remarks>
    /// The legacy page polls by <c>QuoteNbr</c> because it never had the GUID —
    /// its own database stored only the printed quote number. Preserving that
    /// access path is what lets the old page keep working unchanged.
    /// </remarks>
    /// <response code="200">Always. <c>ReturnCode</c> 0 found, 1 not found.</response>
    [HttpGet("quotes/{quoteNbr}")]
    [Authorize(Policy = PolicyFlowPolicies.LegacyIntegration)]
    [ProducesResponseType(typeof(LegacyQuoteStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQuoteStatus(string quoteNbr, CancellationToken ct)
    {
        // Reuses the same read handler the modern endpoint uses, then reshapes it.
        var quote = await getQuote.LookupByQuoteNumberAsync(quoteNbr, ct);

        if (quote is null)
        {
            return Ok(new LegacyQuoteStatusResponse
            {
                ReturnCode = 1,
                ReturnMessage = "QUOTE NOT FOUND"
            });
        }

        return Ok(new LegacyQuoteStatusResponse
        {
            ReturnCode = 0,
            ReturnMessage = "OK",
            QuoteNbr = quote.QuoteNumber,
            QuoteStatus = quote.Status.ToUpperInvariant(),
            // Legacy pages expect fixed-decimal strings, not JSON numbers: the old
            // VB.NET code does CDec() on whatever it receives.
            AnnualPrem = quote.AnnualPremium?.ToString("F2"),
            BestCarrier = quote.BestCarrierId,
            OfferCnt = quote.OfferCount,
            DecisionMsg = quote.DecisionReason,
            CorrelationId = quote.CorrelationId
        });
    }
}

/// <summary>Status response in the legacy field naming and formatting.</summary>
public sealed record LegacyQuoteStatusResponse
{
    /// <summary>0 = found, 1 = not found, 2 = system error.</summary>
    public required int ReturnCode { get; init; }

    public required string ReturnMessage { get; init; }

    public string? QuoteNbr { get; init; }

    /// <summary>Uppercase status, as the legacy UI expects.</summary>
    public string? QuoteStatus { get; init; }

    /// <summary>Premium as a fixed-decimal string, e.g. <c>"1482.50"</c>.</summary>
    public string? AnnualPrem { get; init; }

    public string? BestCarrier { get; init; }

    public int OfferCnt { get; init; }

    public string? DecisionMsg { get; init; }

    public string? CorrelationId { get; init; }
}
