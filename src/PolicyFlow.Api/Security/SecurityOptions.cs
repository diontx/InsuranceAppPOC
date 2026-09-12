namespace PolicyFlow.Api.Security;

/// <summary>
/// Authentication and authorization settings.
/// </summary>
/// <remarks>
/// <para>
/// This POC <b>simulates</b> Microsoft Entra ID with a local symmetric-key JWT
/// issuer, so the whole authorization architecture — bearer tokens, scopes, roles,
/// policies, 401 versus 403 — can be demonstrated and tested without a tenant.
/// </para>
/// <para>
/// The production migration is a configuration change, not a code change: point
/// <see cref="Authority"/> at the Entra ID tenant, keep <see cref="Audience"/>,
/// delete <see cref="SigningKey"/>, and turn <see cref="EnableDevTokenEndpoint"/>
/// off. The <c>[Authorize]</c> attributes and policies are already what Entra ID
/// would drive. See <c>docs/SECURITY.md</c>.
/// </para>
/// </remarks>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Expected <c>iss</c> claim.</summary>
    public string Issuer { get; set; } = "https://policyflow-local-idp";

    /// <summary>Expected <c>aud</c> claim.</summary>
    public string Audience { get; set; } = "policyflow-api";

    /// <summary>
    /// HS256 signing key for the simulated issuer, from <c>JWT_SIGNING_KEY</c>.
    /// Never committed and never logged. In production this is empty and
    /// <see cref="Authority"/> supplies keys over OIDC discovery instead.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// OIDC authority. When set, tokens are validated against its published JWKS
    /// (asymmetric RS256) and <see cref="SigningKey"/> is ignored — the production
    /// path.
    /// </summary>
    public string? Authority { get; set; }

    public int TokenLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Exposes <c>POST /api/auth/dev-token</c>, which mints tokens with no
    /// credential check. <b>Local demos only.</b> Startup refuses to enable it
    /// outside Development, so a misconfigured production deployment fails fast
    /// rather than serving an open token endpoint.
    /// </summary>
    public bool EnableDevTokenEndpoint { get; set; }

    /// <summary>
    /// Exact origins allowed by CORS. A allowlist, never a wildcard: the API is
    /// called with credentials, and <c>Access-Control-Allow-Origin: *</c> cannot be
    /// combined with credentials at all.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// When true, all endpoints require a bearer token. Off by default so the demo
    /// runs without a token step; <c>docs/SECURITY.md</c> explains how to turn it on.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    public int RateLimitPermitPerMinute { get; set; } = 120;

    public int RateLimitQueueLimit { get; set; } = 10;

    /// <summary>True when a local symmetric key is configured.</summary>
    public bool HasLocalSigningKey => !string.IsNullOrWhiteSpace(SigningKey);

    /// <summary>True when validating against a real OIDC authority.</summary>
    public bool UsesExternalAuthority => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Authorization policy names. Constants rather than string literals so a typo in
/// an <c>[Authorize]</c> attribute is a compile error — a misspelled policy name
/// silently fails closed at runtime, which is hard to notice in a happy-path demo.
/// </summary>
public static class PolicyFlowPolicies
{
    /// <summary>Submit quotes. Customers and agents.</summary>
    public const string SubmitQuotes = "quotes:write";

    /// <summary>Read quotes and carrier comparisons.</summary>
    public const string ReadQuotes = "quotes:read";

    /// <summary>Bind a quote into a policy. Licensed agents only.</summary>
    public const string BindPolicies = "policies:write";

    /// <summary>Read policies.</summary>
    public const string ReadPolicies = "policies:read";

    /// <summary>
    /// Take a payment. Separated from policy binding because money movement
    /// deserves its own authorization decision and its own audit trail.
    /// </summary>
    public const string SubmitPayments = "payments:write";

    /// <summary>Legacy system-to-system calls. Service principals, not users.</summary>
    public const string LegacyIntegration = "legacy:integrate";
}

/// <summary>Role names issued by the simulated identity provider.</summary>
public static class PolicyFlowRoles
{
    /// <summary>Self-service customer. Can quote and read their own quote.</summary>
    public const string Customer = "Customer";

    /// <summary>Licensed agent. Can bind policies and take payments.</summary>
    public const string Agent = "Agent";

    /// <summary>Underwriter. Reviews referrals.</summary>
    public const string Underwriter = "Underwriter";

    /// <summary>Service principal for the legacy Web Forms application.</summary>
    public const string LegacySystem = "LegacySystem";
}
