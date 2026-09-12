using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PolicyFlow.Api.Security;

namespace PolicyFlow.Api.Controllers;

/// <summary>
/// Local development token issuer, standing in for Microsoft Entra ID.
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint issues tokens without verifying any credential.</b> It exists
/// so the authorization architecture — bearer tokens, scopes, roles, policies,
/// 401 versus 403 — is demonstrable and testable without provisioning a tenant.
/// </para>
/// <para>Three independent controls keep it out of a deployed environment:</para>
/// <list type="number">
///   <item>The whole controller is compiled in but only routed when
///         <c>Security:EnableDevTokenEndpoint</c> is true.</item>
///   <item><c>SecurityExtensions</c> throws at startup if that flag is true while
///         the environment is not Development, so a misconfigured deployment fails
///         to boot rather than serving an open token endpoint.</item>
///   <item>It requires a local symmetric signing key, which the same startup guard
///         refuses to accept outside Development.</item>
/// </list>
/// <para>
/// Migrating to real Entra ID deletes this file and sets
/// <c>Security:Authority</c>. Nothing else changes: the <c>[Authorize]</c>
/// attributes and policies are already the ones Entra ID would drive.
/// </para>
/// </remarks>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IOptions<SecurityOptions> securityOptions,
    IWebHostEnvironment environment,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>Mints a development bearer token for a chosen role.</summary>
    /// <remarks>
    /// Available only when <c>ENABLE_DEV_TOKEN_ENDPOINT=true</c> in Development.
    /// </remarks>
    /// <response code="200">Token issued.</response>
    /// <response code="404">The endpoint is disabled in this environment.</response>
    [HttpPost("dev-token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(DevTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult IssueDevToken([FromBody] DevTokenRequest? request)
    {
        var security = securityOptions.Value;

        // Defence in depth. The startup guard should already have prevented this
        // combination, but an endpoint that mints credentials re-checks rather than
        // trusting that it was configured correctly.
        if (!security.EnableDevTokenEndpoint || !environment.IsDevelopment() || !security.HasLocalSigningKey)
        {
            // 404, not 403: do not confirm that a dev token endpoint exists here.
            return NotFound();
        }

        var role = NormaliseRole(request?.Role);
        var subject = string.IsNullOrWhiteSpace(request?.Subject)
            ? $"dev-{role.ToLowerInvariant()}"
            : request.Subject.Trim();

        var scopes = ScopesForRole(role);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Subject, subject),
            new("name", subject),
            new("roles", role),
            new("scope", string.Join(' ', scopes)),
            // A unique token id, so an individual token could be revoked by jti in
            // a system that maintained a denylist.
            new(JwtRegisteredClaimNames.JwtId, Guid.NewGuid().ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(security.SigningKey));
        var expiresUtc = DateTime.UtcNow.AddMinutes(security.TokenLifetimeMinutes);

        var token = new JwtSecurityToken(
            issuer: security.Issuer,
            audience: security.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresUtc,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);

        // The role and lifetime are logged; the token is not. A bearer token in a
        // log file is a usable credential for anyone who can read the log.
        logger.LogWarning(
            "Development token issued without credential verification. Operation={Operation} " +
            "Role={Role} Subject={Subject} ExpiresUtc={ExpiresUtc} Result={Result}",
            "IssueDevToken", role, subject, expiresUtc, "Issued");

        return Ok(new DevTokenResponse
        {
            AccessToken = encoded,
            TokenType = "Bearer",
            ExpiresInSeconds = security.TokenLifetimeMinutes * 60,
            Role = role,
            Scopes = scopes,
            Warning = "Development token. Issued with no credential check. Never use outside local development."
        });
    }

    /// <summary>Reports the claims on the presented token. Useful for debugging 403s.</summary>
    /// <response code="200">Claims on the current token.</response>
    /// <response code="401">No valid bearer token.</response>
    [HttpGet("whoami")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult WhoAmI() => Ok(new
    {
        name = User.Identity?.Name,
        isAuthenticated = User.Identity?.IsAuthenticated ?? false,
        roles = User.FindAll("roles").Select(c => c.Value).ToArray(),
        scopes = User.FindFirst("scope")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []
    });

    /// <summary>Maps a requested role to a known one, defaulting to Agent.</summary>
    private static string NormaliseRole(string? requested) => requested?.Trim() switch
    {
        var r when string.Equals(r, PolicyFlowRoles.Customer, StringComparison.OrdinalIgnoreCase)
            => PolicyFlowRoles.Customer,
        var r when string.Equals(r, PolicyFlowRoles.Underwriter, StringComparison.OrdinalIgnoreCase)
            => PolicyFlowRoles.Underwriter,
        var r when string.Equals(r, PolicyFlowRoles.LegacySystem, StringComparison.OrdinalIgnoreCase)
            => PolicyFlowRoles.LegacySystem,
        // Agent is the demo default: it can walk the full quote-to-payment flow.
        _ => PolicyFlowRoles.Agent
    };

    /// <summary>
    /// Scopes granted per role. Least privilege: a Customer token cannot bind a
    /// policy or move money, and the legacy service principal can only submit
    /// quotes through its seam.
    /// </summary>
    private static string[] ScopesForRole(string role) => role switch
    {
        PolicyFlowRoles.Customer =>
        [
            PolicyFlowPolicies.SubmitQuotes,
            PolicyFlowPolicies.ReadQuotes
        ],

        PolicyFlowRoles.Underwriter =>
        [
            PolicyFlowPolicies.ReadQuotes,
            PolicyFlowPolicies.ReadPolicies
        ],

        PolicyFlowRoles.LegacySystem =>
        [
            PolicyFlowPolicies.LegacyIntegration,
            PolicyFlowPolicies.SubmitQuotes,
            PolicyFlowPolicies.ReadQuotes
        ],

        // Agent: the full self-service flow.
        _ =>
        [
            PolicyFlowPolicies.SubmitQuotes,
            PolicyFlowPolicies.ReadQuotes,
            PolicyFlowPolicies.BindPolicies,
            PolicyFlowPolicies.ReadPolicies,
            PolicyFlowPolicies.SubmitPayments
        ]
    };
}

public sealed record DevTokenRequest
{
    /// <summary>One of Customer, Agent, Underwriter, LegacySystem. Defaults to Agent.</summary>
    /// <example>Agent</example>
    public string? Role { get; init; }

    /// <summary>Subject claim. Defaults to a value derived from the role.</summary>
    public string? Subject { get; init; }
}

public sealed record DevTokenResponse
{
    public required string AccessToken { get; init; }
    public required string TokenType { get; init; }
    public required int ExpiresInSeconds { get; init; }
    public required string Role { get; init; }
    public required string[] Scopes { get; init; }
    public required string Warning { get; init; }
}

/// <summary>
/// JWT registered claim names. Spelled out rather than using
/// <c>JwtRegisteredClaimNames</c> from the library so the wire format is visible
/// at the call site.
/// </summary>
internal static class JwtRegisteredClaimNames
{
    public const string Subject = "sub";
    public const string JwtId = "jti";
}
