using PolicyFlow.Application.Abstractions;

namespace PolicyFlow.Api.Middleware;

/// <summary>
/// Per-request correlation id holder. Scoped, so one instance serves one request
/// and every handler in that request sees the same value.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; private set; } = Guid.NewGuid().ToString();

    internal void Set(string correlationId) => CorrelationId = correlationId;
}

/// <summary>
/// Establishes a correlation id for every request and echoes it back.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a production incident tractable. One id flows from the HTTP
/// request, into the SQL rows, onto the Service Bus message, and into the worker's
/// logs — so "this customer's quote was slow" becomes a single log query instead of
/// guessing which of five carrier calls was the problem.
/// </para>
/// <para>
/// A client-supplied <c>X-Correlation-Id</c> is accepted so a caller can stitch its
/// own trace to ours, but it is <b>validated first</b>. The value lands in log
/// output and in a response header, so an unvalidated one is a log-injection and
/// header-injection vector: a newline would let a caller forge log lines, and an
/// oversized value would bloat every log record for the request. Anything that is
/// not a short, safe token is replaced with a generated GUID rather than rejected,
/// since a bad trace id is no reason to fail a customer's quote.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Long enough for a GUID or a W3C trace id, short enough to be safe in logs.</summary>
    private const int MaximumLength = 64;

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlationContext)
    {
        var correlationId = ResolveCorrelationId(context, logger);
        correlationContext.Set(correlationId);

        // Registered before the response starts, because headers cannot be added
        // once the first byte has been written.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Pushed into the logging scope so every log line in this request carries
        // the id without each call site having to pass it.
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path.Value ?? string.Empty
        }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context, ILogger logger)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Guid.NewGuid().ToString();
        }

        var supplied = values.ToString();

        if (string.IsNullOrWhiteSpace(supplied))
        {
            return Guid.NewGuid().ToString();
        }

        if (supplied.Length > MaximumLength || !IsSafeToken(supplied))
        {
            // Do not echo the rejected value into the log message — that would be
            // the injection this check exists to prevent.
            logger.LogWarning(
                "Supplied {HeaderName} was rejected as unsafe and replaced with a generated id. " +
                "Operation={Operation} SuppliedLength={SuppliedLength} Result={Result}",
                HeaderName, "ResolveCorrelationId", supplied.Length, "Replaced");

            return Guid.NewGuid().ToString();
        }

        return supplied;
    }

    /// <summary>
    /// Allows only characters that appear in GUIDs, W3C trace ids and similar
    /// tokens. Excludes CR, LF, control characters and anything that could break
    /// a log line or an HTTP header.
    /// </summary>
    private static bool IsSafeToken(string value)
    {
        foreach (var c in value)
        {
            var isAllowed = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':';
            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }
}
