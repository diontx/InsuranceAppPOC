using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Payments;
using PolicyFlow.Application.Policies;
using PolicyFlow.Domain.Common;

namespace PolicyFlow.Api.Middleware;

/// <summary>
/// Translates every unhandled exception into an RFC 9457 <c>ProblemDetails</c>
/// response.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, and the security one matters more. First, map domain failures to the
/// right status code so clients can act on them. Second, and non-negotiably:
/// <b>never leak internals to a caller</b>. Stack traces, SQL text, connection
/// strings and type names all reveal the shape of the system to an attacker, so the
/// full exception goes to the structured log and the client gets a generic message
/// plus the correlation id to quote to support.
/// </para>
/// <para>The status-code mapping:</para>
/// <list type="table">
///   <item><term>400</term><description>Malformed request; a missing idempotency key.</description></item>
///   <item><term>404</term><description>Quote or policy id does not exist.</description></item>
///   <item><term>409</term><description>Concurrency conflict or duplicate business operation.</description></item>
///   <item><term>422</term><description>Well-formed request that violates a business rule.</description></item>
///   <item><term>503</term><description>A simulated external dependency is unavailable.</description></item>
///   <item><term>500</term><description>Anything unrecognised. Details stay in the logs.</description></item>
/// </list>
/// </remarks>
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken ct)
    {
        var correlationId = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = context.RequestServices
                .GetService<ICorrelationContext>()?.CorrelationId ?? "unknown";
        }

        var problem = Map(exception, environment.IsDevelopment());

        // The full exception, including the stack trace, goes here and nowhere else.
        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "Unhandled exception. Operation={Operation} Path={Path} Status={Status} " +
                "CorrelationId={CorrelationId} Result={Result}",
                "RequestPipeline", context.Request.Path, problem.Status, correlationId, "Error");
        }
        else
        {
            logger.LogWarning(
                "Request failed with a handled error. Operation={Operation} Path={Path} " +
                "Status={Status} Reason={Reason} CorrelationId={CorrelationId} Result={Result}",
                "RequestPipeline", context.Request.Path, problem.Status, exception.Message,
                correlationId, "Rejected");
        }

        problem.Instance = context.Request.Path;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(problem, ct);

        return true;
    }

    private static ProblemDetails Map(Exception exception, bool isDevelopment) => exception switch
    {
        MissingIdempotencyKeyException => new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Idempotency key required",
            Type = "https://policyflow.example/problems/missing-idempotency-key",
            Detail = "Submit an Idempotency-Key header. Reuse the same key when retrying " +
                     "the same payment so the transaction is not duplicated."
        },

        QuoteNotFoundException => new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Quote not found",
            Type = "https://policyflow.example/problems/quote-not-found",
            Detail = "No quote exists with the supplied identifier."
        },

        PolicyNotFoundException => new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Policy not found",
            Type = "https://policyflow.example/problems/policy-not-found",
            Detail = "No policy exists with the supplied identifier."
        },

        // Checked before DomainException: BusinessConflictException derives from it,
        // and switch arms match in order.
        BusinessConflictException conflict => new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Conflicting operation",
            Type = "https://policyflow.example/problems/conflict",
            // Safe to surface: these messages are authored in the domain layer and
            // describe business state, not internals.
            Detail = conflict.Message
        },

        DomainException domain => new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Business rule violation",
            Type = "https://policyflow.example/problems/business-rule",
            Detail = domain.Message
        },

        DependencyUnavailableException => new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Dependency unavailable",
            Type = "https://policyflow.example/problems/dependency-unavailable",
            Detail = "An external verification service is temporarily unavailable. Please retry."
        },

        OperationCanceledException => new ProblemDetails
        {
            Status = StatusCodesExtra.ClientClosedRequest,
            Title = "Request cancelled",
            Detail = "The request was cancelled before it completed."
        },

        // Anything unrecognised. The message is deliberately generic: an exception
        // from EF Core or the SQL client can contain schema names, parameter values
        // or connection details, none of which belong in a client response.
        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred",
            Type = "https://policyflow.example/problems/internal-error",
            Detail = isDevelopment
                // Even in Development this is the message only, never the stack trace.
                ? $"{exception.GetType().Name}: {exception.Message}"
                : "An unexpected error occurred. Quote the correlation id when contacting support."
        }
    };
}

/// <summary>
/// Status codes ASP.NET Core does not define as constants.
/// </summary>
internal static class StatusCodesExtra
{
    /// <summary>Nginx's convention for a client that disconnected mid-request.</summary>
    public const int ClientClosedRequest = 499;
}
