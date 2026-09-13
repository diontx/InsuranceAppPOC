using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace PolicyFlow.Api.Security;

/// <summary>
/// Registers authentication, authorization, CORS and rate limiting.
/// </summary>
public static class SecurityExtensions
{
    /// <summary>Rate-limit partition name for unauthenticated callers.</summary>
    public const string RateLimitPolicyName = "policyflow-fixed";

    public static IServiceCollection AddPolicyFlowSecurity(
        this IServiceCollection services,
        SecurityOptions security,
        IWebHostEnvironment environment)
    {
        // ====================================================================
        // Fail-fast configuration guards.
        // Misconfigured security must break the build-out, not quietly weaken
        // production. Each of these has a real-world failure mode behind it.
        // ====================================================================
        if (!environment.IsDevelopment() && security.EnableDevTokenEndpoint)
        {
            throw new InvalidOperationException(
                "ENABLE_DEV_TOKEN_ENDPOINT is true outside Development. The dev token endpoint " +
                "issues tokens without verifying any credential and must never be reachable in a " +
                "deployed environment. Set ENABLE_DEV_TOKEN_ENDPOINT=false.");
        }

        if (!environment.IsDevelopment() && security.HasLocalSigningKey && !security.UsesExternalAuthority)
        {
            throw new InvalidOperationException(
                "A local symmetric signing key is configured outside Development. Symmetric keys mean " +
                "the API can mint the tokens it accepts, so a leaked key forges any identity. " +
                "Configure Security:Authority (Entra ID) and remove JWT_SIGNING_KEY.");
        }

        if (security.HasLocalSigningKey && Encoding.UTF8.GetByteCount(security.SigningKey) < 32)
        {
            // HS256 requires a key at least as long as the hash output. A short key
            // is brute-forceable and the JWT library will reject it at runtime
            // anyway — better to say why now.
            throw new InvalidOperationException(
                "JWT_SIGNING_KEY must be at least 32 bytes for HS256.");
        }

        // --- Authentication.
        var authBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        if (security.UsesExternalAuthority || security.HasLocalSigningKey)
        {
            authBuilder.AddJwtBearer(options =>
            {
                // Tokens are bearer credentials: HTTPS only, except for local
                // container-to-container traffic in Development.
                options.RequireHttpsMetadata = !environment.IsDevelopment();
                options.Audience = security.Audience;

                if (security.UsesExternalAuthority)
                {
                    // Production path: keys come from the authority's JWKS endpoint
                    // and rotate without a redeploy.
                    options.Authority = security.Authority;
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = security.Issuer,

                    ValidateAudience = true,
                    ValidAudience = security.Audience,

                    // Non-negotiable. Disabling lifetime validation makes every
                    // token ever issued valid forever.
                    ValidateLifetime = true,
                    // Five minutes is the default; two is tighter while still
                    // tolerating normal clock drift between containers.
                    ClockSkew = TimeSpan.FromMinutes(2),

                    ValidateIssuerSigningKey = true,

                    IssuerSigningKey = security.HasLocalSigningKey && !security.UsesExternalAuthority
                        ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(security.SigningKey))
                        : null,

                    // Pin the algorithm. Without this, a token presenting
                    // "alg": "none" or a confused-algorithm variant can bypass
                    // signature verification entirely.
                    ValidAlgorithms = security.UsesExternalAuthority
                        ? [SecurityAlgorithms.RsaSha256]
                        : [SecurityAlgorithms.HmacSha256],

                    RoleClaimType = "roles",
                    NameClaimType = "name"
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // Log that validation failed and why in general terms. The
                        // token itself is never logged: it is a live credential.
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerHandler>>();

                        logger.LogWarning(
                            "Bearer token validation failed. Operation={Operation} " +
                            "Reason={Reason} Path={Path} Result={Result}",
                            "AuthenticateRequest", context.Exception.GetType().Name,
                            context.HttpContext.Request.Path, "Unauthenticated");

                        return Task.CompletedTask;
                    },

                    OnChallenge = context =>
                    {
                        // Suppress the default WWW-Authenticate detail, which can
                        // describe exactly why a token was rejected and helps an
                        // attacker iterate.
                        context.Response.Headers.Remove("WWW-Authenticate");
                        return Task.CompletedTask;
                    }
                };
            });
        }

        // --- Authorization policies.
        // Each maps to a scope claim the identity provider issues. Scopes are
        // checked rather than roles alone so a delegated token can be narrower
        // than the user's full role.
        //
        // When RequireAuthentication is false (the default for the local demo and
        // the integration tests), every policy is registered as permissive. The
        // policies still EXIST with their real names and are still wired to the
        // same [Authorize] attributes, so enabling enforcement is a configuration
        // change with no code change — but the demo can be walked without a token
        // step. The startup log says plainly when this is in effect.
        var relaxed = !security.RequireAuthentication;

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyFlowPolicies.SubmitQuotes, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.SubmitQuotes) ||
                    context.User.IsInRole(PolicyFlowRoles.Customer) ||
                    context.User.IsInRole(PolicyFlowRoles.Agent)));

            options.AddPolicy(PolicyFlowPolicies.ReadQuotes, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.ReadQuotes) ||
                    context.User.IsInRole(PolicyFlowRoles.Customer) ||
                    context.User.IsInRole(PolicyFlowRoles.Agent) ||
                    context.User.IsInRole(PolicyFlowRoles.Underwriter)));

            // Binding is agent-only: a self-service customer token must not be able
            // to create a financial obligation.
            options.AddPolicy(PolicyFlowPolicies.BindPolicies, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.BindPolicies) ||
                    context.User.IsInRole(PolicyFlowRoles.Agent)));

            options.AddPolicy(PolicyFlowPolicies.ReadPolicies, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.ReadPolicies) ||
                    context.User.IsInRole(PolicyFlowRoles.Agent) ||
                    context.User.IsInRole(PolicyFlowRoles.Underwriter)));

            // Money movement gets its own policy so it can be revoked
            // independently of the ability to read or bind a policy.
            options.AddPolicy(PolicyFlowPolicies.SubmitPayments, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.SubmitPayments) ||
                    context.User.IsInRole(PolicyFlowRoles.Agent)));

            // The legacy application authenticates as a service principal, not as
            // a user, so it gets a dedicated role and policy. That keeps the
            // eventual retirement of the legacy channel a one-line revocation.
            // Underwriters only. An agent can bind what an underwriter approved, but
            // must not be able to approve their own referral.
            options.AddPolicy(PolicyFlowPolicies.ReviewReferrals, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.ReviewReferrals) ||
                    context.User.IsInRole(PolicyFlowRoles.Underwriter)));

            options.AddPolicy(PolicyFlowPolicies.LegacyIntegration, policy =>
                policy.RequireAssertion(context => relaxed ||
                    HasScope(context.User, PolicyFlowPolicies.LegacyIntegration) ||
                    context.User.IsInRole(PolicyFlowRoles.LegacySystem)));

            if (relaxed)
            {
                // Covers bare [Authorize] with no named policy, which would
                // otherwise still demand an authenticated user.
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAssertion(_ => true)
                    .Build();
            }
        });

        // --- CORS.
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (security.AllowedOrigins.Length == 0)
                {
                    // No configured origins means no cross-origin access, rather
                    // than falling back to allowing everything.
                    return;
                }

                policy
                    .WithOrigins(security.AllowedOrigins)
                    .WithMethods("GET", "POST", "OPTIONS")
                    // Explicit header allowlist, not AllowAnyHeader.
                    .WithHeaders(
                        "Content-Type",
                        "Authorization",
                        "Idempotency-Key",
                        Middleware.CorrelationIdMiddleware.HeaderName)
                    // The SPA reads the correlation id off the response to display
                    // for support, so it must be explicitly exposed — browsers hide
                    // non-safelisted response headers otherwise.
                    .WithExposedHeaders(Middleware.CorrelationIdMiddleware.HeaderName, "Location")
                    .AllowCredentials()
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            });
        });

        // --- Rate limiting.
        // Blunt but effective: caps how fast one caller can hit the API, which
        // limits both accidental retry storms and credential-stuffing throughput.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    // Partition by authenticated identity when present, otherwise by
                    // remote IP. Partitioning by IP alone would let one user behind a
                    // shared NAT exhaust the limit for everyone on it.
                    partitionKey: context.User.Identity?.IsAuthenticated == true
                        ? context.User.Identity.Name ?? "authenticated"
                        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = security.RateLimitPermitPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = security.RateLimitQueueLimit
                    }));

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";

                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<SecurityOptions>>();

                logger.LogWarning(
                    "Request rate limited. Operation={Operation} Path={Path} Result={Result}",
                    "RateLimit", context.HttpContext.Request.Path, "TooManyRequests");

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://policyflow.example/problems/rate-limited",
                    title = "Too many requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "Request rate exceeded. Retry after 60 seconds."
                }, ct);
            };
        });

        return services;
    }

    /// <summary>
    /// Checks for a scope in the OAuth 2.0 <c>scope</c> claim, which is a single
    /// space-delimited string rather than one claim per scope.
    /// </summary>
    private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope)
    {
        var scopeClaim = user.FindFirst("scope")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;

        return scopeClaim is not null
            && scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(requiredScope, StringComparer.Ordinal);
    }
}
