namespace PolicyFlow.Api.Middleware;

/// <summary>
/// Adds defensive HTTP response headers.
/// </summary>
/// <remarks>
/// <para>
/// These are cheap, and each one closes a specific class of attack. They matter
/// even on a JSON API, because a browser that can be persuaded to render an API
/// response as HTML turns a reflected value into stored XSS.
/// </para>
/// <para>
/// In a real deployment these would usually be set at the reverse proxy or
/// gateway. They are set in the application here so the POC is self-contained and
/// the intent is visible in source rather than in infrastructure config.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Stops a browser from second-guessing Content-Type. Without it, a JSON
        // response containing markup can be sniffed as HTML and executed.
        headers["X-Content-Type-Options"] = "nosniff";

        // Blocks framing, defeating clickjacking. CSP frame-ancestors below is the
        // modern equivalent; this covers older browsers.
        headers["X-Frame-Options"] = "DENY";

        // Sends no referrer cross-origin, so quote ids and policy numbers in a URL
        // do not leak to third-party sites in the Referer header.
        headers["Referrer-Policy"] = "no-referrer";

        // Denies access to device APIs this API has no use for.
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=(), payment=()";

        // API responses are per-customer and must never be cached by a shared proxy.
        headers["Cache-Control"] = "no-store, no-cache, must-revalidate";

        // A restrictive CSP for a JSON API: nothing should ever be loaded or
        // executed from an API response, so deny everything.
        headers["Content-Security-Policy"] =
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        // Advertises the API version without revealing the framework or host OS.
        // The default Server and X-Powered-By headers are suppressed in Program.cs;
        // telling an attacker the exact runtime build is free reconnaissance.
        headers.Remove("X-Powered-By");
        headers.Remove("Server");

        await next(context);
    }
}
