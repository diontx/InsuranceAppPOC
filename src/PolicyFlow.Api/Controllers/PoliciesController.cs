using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Api.Contracts;
using PolicyFlow.Api.Security;
using PolicyFlow.Application.Payments;
using PolicyFlow.Application.Policies;

namespace PolicyFlow.Api.Controllers;

/// <summary>Policy binding, retrieval and premium payment.</summary>
[ApiController]
[Route("api/policies")]
[Produces("application/json")]
public sealed class PoliciesController(
    CreatePolicyFromQuoteHandler createPolicy,
    GetPolicyHandler getPolicy,
    SubmitPaymentHandler submitPayment) : ControllerBase
{
    /// <summary>Binds a rated quote into a policy.</summary>
    /// <remarks>
    /// The quote must be <c>Rated</c>, and a quote can back at most one policy — a
    /// UNIQUE index on <c>Policy.QuoteId</c> enforces that, so a duplicate attempt
    /// returns 409 even when two requests race.
    /// </remarks>
    /// <response code="201">Policy created.</response>
    /// <response code="404">No such quote.</response>
    /// <response code="409">This quote is already bound to a policy.</response>
    /// <response code="422">The quote is not Rated, or the offer does not belong to it.</response>
    [HttpPost("from-quote/{quoteId:guid}")]
    [Authorize(Policy = PolicyFlowPolicies.BindPolicies)]
    [ProducesResponseType(typeof(PolicyView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateFromQuote(
        Guid quoteId,
        [FromBody] CreatePolicyRequest? request,
        CancellationToken ct)
    {
        var policy = await createPolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand
            {
                QuoteId = quoteId,
                OfferId = request?.OfferId,
                EffectiveDate = request?.EffectiveDate
            },
            ct);

        return CreatedAtAction(nameof(Get), new { policyId = policy.PolicyId }, policy);
    }

    /// <summary>Reads a policy with its payment history.</summary>
    /// <response code="200">Policy detail.</response>
    /// <response code="404">No such policy.</response>
    [HttpGet("{policyId:guid}")]
    [Authorize(Policy = PolicyFlowPolicies.ReadPolicies)]
    [ProducesResponseType(typeof(PolicyView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid policyId, CancellationToken ct)
    {
        var policy = await getPolicy.HandleAsync(policyId, ct);

        return policy is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Policy not found",
                Type = "https://policyflow.example/problems/policy-not-found",
                Detail = $"No policy exists with identifier {policyId}."
            })
            : Ok(policy);
    }

    /// <summary>
    /// Takes a premium payment. Requires an <c>Idempotency-Key</c> header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Submitting the same <c>Idempotency-Key</c> twice returns the <i>original</i>
    /// transaction with <c>wasReplayed: true</c> and creates nothing new. The
    /// guarantee is a UNIQUE index on <c>(PolicyId, IdempotencyKey)</c> in SQL
    /// Server, so it holds under genuine concurrency across multiple API instances
    /// — not just for a double-click on one server.
    /// </para>
    /// <para>
    /// Generate one key per payment <i>intent</i> and reuse it for every retry of
    /// that intent. A new key per HTTP attempt would defeat the mechanism entirely.
    /// </para>
    /// </remarks>
    /// <response code="200">Payment recorded, or the original replayed.</response>
    /// <response code="400">The <c>Idempotency-Key</c> header is missing.</response>
    /// <response code="404">No such policy.</response>
    /// <response code="422">The policy is not Active, or the amount is invalid.</response>
    [HttpPost("{policyId:guid}/payments")]
    [Authorize(Policy = PolicyFlowPolicies.SubmitPayments)]
    [ProducesResponseType(typeof(PaymentView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SubmitPayment(
        Guid policyId,
        [FromBody] SubmitPaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        // The handler also guards this; checking here produces a clearer 400 with
        // the header name before any database work happens.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new MissingIdempotencyKeyException();
        }

        var payment = await submitPayment.HandleAsync(
            new SubmitPaymentCommand
            {
                PolicyId = policyId,
                Amount = request.Amount,
                IdempotencyKey = idempotencyKey
            },
            ct);

        // 200, not 201, even on the first call: the response is the state of the
        // payment intent, and a replay must return the identical status code so a
        // retrying client cannot tell the difference.
        return Ok(payment);
    }
}
