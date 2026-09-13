using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Api.Contracts;
using PolicyFlow.Api.Security;
using PolicyFlow.Application.Underwriting;

namespace PolicyFlow.Api.Controllers;

/// <summary>
/// The underwriter work queue: referred quotes, their review detail, and decisions.
/// </summary>
/// <remarks>
/// <para>
/// Consumed by the Angular underwriting workbench hosted inside the legacy Web
/// Forms portal. It is a <b>modern</b> endpoint (<c>/api</c>, camelCase,
/// ProblemDetails), not a legacy-seam one: new screens added to the legacy shell are
/// built against the platform they will outlive the shell on, so nothing has to be
/// rewritten when the Web Forms host is retired.
/// </para>
/// <para>
/// Like every controller here it does HTTP and nothing else. Eligibility traces,
/// carrier appetite and the decision rules all come from the domain.
/// </para>
/// </remarks>
[ApiController]
[Route("api/underwriting")]
[Produces("application/json")]
public sealed class UnderwritingController(
    GetUnderwritingQueueHandler queue,
    GetUnderwritingReviewHandler reviews,
    ReviewReferralHandler reviewReferral,
    IValidator<ReviewReferralCommand> validator) : ControllerBase
{
    /// <summary>
    /// Recorded as the decision-maker when authentication is relaxed for the local
    /// demo. Deliberately says so, so an unauthenticated decision is never mistaken
    /// for a real person's in the data.
    /// </summary>
    public const string UnauthenticatedUnderwriter = "demo-underwriter (unauthenticated)";

    /// <summary>Lists quotes awaiting an underwriter, oldest first.</summary>
    /// <response code="200">The work queue.</response>
    [HttpGet("queue")]
    [Authorize(Policy = PolicyFlowPolicies.ReviewReferrals)]
    [ProducesResponseType(typeof(UnderwritingQueueView), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueue(CancellationToken ct) =>
        Ok(await queue.HandleAsync(ct));

    /// <summary>Reads one quote for review.</summary>
    /// <remarks>
    /// Includes the eligibility trace the pipeline produced and which carriers would
    /// price the risk on approval, so the underwriter can see before deciding
    /// whether an approval can succeed.
    /// </remarks>
    /// <response code="200">Review detail.</response>
    /// <response code="404">No such quote.</response>
    [HttpGet("quotes/{quoteId:guid}")]
    [Authorize(Policy = PolicyFlowPolicies.ReviewReferrals)]
    [ProducesResponseType(typeof(UnderwritingReviewView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview(Guid quoteId, CancellationToken ct)
    {
        var view = await reviews.HandleAsync(quoteId, ct);

        return view is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Quote not found",
                Type = "https://policyflow.example/problems/quote-not-found",
                Detail = $"No quote exists with identifier {quoteId}."
            })
            : Ok(view);
    }

    /// <summary>Records an underwriter decision on a referral.</summary>
    /// <remarks>
    /// <c>Approve</c> prices the risk across the carrier panel and makes the quote
    /// <c>Rated</c>, after which it binds like any other quote. <c>FollowUp</c> keeps
    /// it in the queue with a customer-visible request. <c>Decline</c> closes it.
    /// </remarks>
    /// <response code="200">Decision recorded. Returns the updated review.</response>
    /// <response code="400">Unknown decision, or a missing or too-short note.</response>
    /// <response code="404">No such quote.</response>
    /// <response code="409">The quote was already decided, possibly by another underwriter.</response>
    /// <response code="422">Approval attempted on a risk no carrier will write.</response>
    [HttpPost("quotes/{quoteId:guid}/decision")]
    [Authorize(Policy = PolicyFlowPolicies.ReviewReferrals)]
    [ProducesResponseType(typeof(UnderwritingReviewView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Decide(
        Guid quoteId,
        [FromBody] ReviewDecisionRequest request,
        CancellationToken ct)
    {
        // Enum.TryParse alone would accept "7" or "1,2"; require a defined name.
        if (!Enum.TryParse<ReviewDecision>(request.Decision, ignoreCase: true, out var decision) ||
            !Enum.IsDefined(decision) ||
            int.TryParse(request.Decision, out _))
        {
            ModelState.AddModelError(
                nameof(ReviewDecisionRequest.Decision),
                "Decision must be Approve, FollowUp or Decline.");
            return ValidationProblem(ModelState);
        }

        var command = new ReviewReferralCommand
        {
            QuoteId = quoteId,
            Decision = decision,
            Underwriter = ResolveUnderwriter(),
            Note = request.Note
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

        return Ok(await reviewReferral.HandleAsync(command, ct));
    }

    /// <summary>The decision-maker, from the token and never from the request.</summary>
    private string ResolveUnderwriter() =>
        User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : UnauthenticatedUnderwriter;
}
