using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Api.Contracts;
using PolicyFlow.Api.Middleware;
using PolicyFlow.Api.Security;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Quotes;

namespace PolicyFlow.Api.Controllers;

/// <summary>
/// Quote submission, status polling and carrier comparison.
/// </summary>
/// <remarks>
/// Controllers here do HTTP and nothing else: bind, validate, delegate, map a
/// status code. No rating rules, no EF Core queries. That is what lets the legacy
/// adapter reuse the same logic without duplicating a single business rule.
/// </remarks>
[ApiController]
[Route("api/quotes")]
[Produces("application/json")]
public sealed class QuotesController(
    SubmitQuoteHandler submitQuote,
    GetQuoteHandler getQuote,
    GetQuoteOffersHandler getOffers,
    IValidator<SubmitQuoteCommand> validator) : ControllerBase
{
    /// <summary>Submits a quote for asynchronous underwriting.</summary>
    /// <remarks>
    /// Returns <c>202 Accepted</c>, not <c>201 Created</c>: the quote exists but its
    /// price does not yet. Underwriting calls a verification bureau and five carrier
    /// gateways, so holding the request open would tie a customer's page load to the
    /// slowest third party in the chain. Poll <c>statusUrl</c> for the outcome.
    /// </remarks>
    /// <response code="202">Accepted. Poll the status URL.</response>
    /// <response code="400">Validation failure. See <c>errors</c> for per-field detail.</response>
    /// <response code="422">Well-formed but violates a business rule.</response>
    /// <response code="429">Rate limit exceeded.</response>
    [HttpPost]
    [Authorize(Policy = PolicyFlowPolicies.SubmitQuotes)]
    [ProducesResponseType(typeof(SubmitQuoteResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitQuoteRequest request,
        [FromHeader(Name = CorrelationIdMiddleware.HeaderName)] string? correlationId,
        CancellationToken ct)
    {
        var command = new SubmitQuoteCommand
        {
            CustomerName = request.CustomerName,
            State = request.State,
            VehicleYear = request.VehicleYear,
            VehicleMake = request.VehicleMake,
            VehicleModel = request.VehicleModel,
            AnnualMileage = request.AnnualMileage,
            PriorInsuranceMonths = request.PriorInsuranceMonths,
            Channel = Domain.Quotes.QuoteChannel.Modern
        };

        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
            }

            return ValidationProblem(ModelState);
        }

        var result = await submitQuote.HandleAsync(command, ct);

        // Location points at the status resource so a client can follow the
        // standard "202 + poll Location" pattern without reading the body.
        return Accepted(
            result.StatusUrl,
            new SubmitQuoteResponse
            {
                QuoteId = result.QuoteId,
                QuoteNumber = result.QuoteNumber,
                Status = result.Status,
                StatusUrl = result.StatusUrl,
                CorrelationId = result.CorrelationId
            });
    }

    /// <summary>Reads a quote's underwriting status.</summary>
    /// <remarks>
    /// The SPA polls this every 1.5 seconds and stops when <c>isTerminal</c> is
    /// true. A single primary-key read, so polling is cheap.
    /// </remarks>
    /// <response code="200">Current quote status.</response>
    /// <response code="404">No such quote.</response>
    [HttpGet("{quoteId:guid}")]
    [Authorize(Policy = PolicyFlowPolicies.ReadQuotes)]
    [ProducesResponseType(typeof(QuoteStatusView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid quoteId, CancellationToken ct)
    {
        var view = await getQuote.HandleAsync(quoteId, ct);
        return view is null ? NotFound(NotFoundProblem(quoteId)) : Ok(view);
    }

    /// <summary>Reads the five-carrier comparison for a rated quote.</summary>
    /// <remarks>
    /// Served from the MongoDB read model as a single document — up to fifteen
    /// priced options with limits, discounts and a rating trace, plus the carriers
    /// that declined and why. Falls back to projecting from SQL if the read model
    /// is unavailable.
    /// </remarks>
    /// <response code="200">Carrier comparison.</response>
    /// <response code="404">No such quote.</response>
    [HttpGet("{quoteId:guid}/offers")]
    [Authorize(Policy = PolicyFlowPolicies.ReadQuotes)]
    [ProducesResponseType(typeof(QuoteComparisonDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOffers(Guid quoteId, CancellationToken ct)
    {
        var comparison = await getOffers.HandleAsync(quoteId, ct);
        return comparison is null ? NotFound(NotFoundProblem(quoteId)) : Ok(comparison);
    }

    /// <summary>
    /// Mobile self-service lookup by quote number and surname.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Powers the mobile "check my quote" screen, where a customer has a text
    /// message with a quote number and no session.
    /// </para>
    /// <para>
    /// Two factors are required. Quote numbers are sequential and therefore
    /// guessable, so the surname acts as a shared secret. A wrong surname returns
    /// the same 404 as a wrong quote number, which avoids confirming that a given
    /// quote number exists. The endpoint is also rate limited.
    /// </para>
    /// <para>
    /// This is adequate for a POC, not for production: real deployments should put
    /// this behind customer authentication or a one-time link. See
    /// <c>docs/SECURITY.md</c>.
    /// </para>
    /// </remarks>
    /// <response code="200">Quote status with its carrier comparison.</response>
    /// <response code="400">Missing quote number or surname.</response>
    /// <response code="404">No quote matches both values.</response>
    [HttpGet("lookup")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(QuoteLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Lookup(
        [FromQuery] string? quoteNumber,
        [FromQuery] string? lastName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(quoteNumber) || string.IsNullOrWhiteSpace(lastName))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Quote number and last name are required",
                Detail = "Supply both quoteNumber and lastName query parameters."
            });
        }

        var status = await getQuote.LookupAsync(quoteNumber, lastName, ct);
        if (status is null)
        {
            // Same response for "no such quote" and "surname does not match", so the
            // endpoint cannot be used to enumerate valid quote numbers.
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Quote not found",
                Detail = "No quote matches that quote number and last name."
            });
        }

        // Only fetch the comparison once the quote is actually rated.
        var comparison = status.IsTerminal && status.OfferCount > 0
            ? await getOffers.HandleAsync(status.QuoteId, ct)
            : null;

        return Ok(new QuoteLookupResponse { Quote = status, Comparison = comparison });
    }

    private static ProblemDetails NotFoundProblem(Guid quoteId) => new()
    {
        Status = StatusCodes.Status404NotFound,
        Title = "Quote not found",
        Type = "https://policyflow.example/problems/quote-not-found",
        Detail = $"No quote exists with identifier {quoteId}."
    };
}

/// <summary>
/// Combined response for the mobile lookup: status plus comparison in one round
/// trip, because a phone on a slow connection should not need two.
/// </summary>
public sealed record QuoteLookupResponse
{
    public required QuoteStatusView Quote { get; init; }

    /// <summary>Null unless the quote is rated with offers.</summary>
    public QuoteComparisonDocument? Comparison { get; init; }
}
