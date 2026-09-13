using System.ComponentModel.DataAnnotations;

namespace PolicyFlow.Api.Contracts;

/// <summary>
/// Request body for <c>POST /api/underwriting/quotes/{quoteId}/decision</c>.
/// </summary>
/// <remarks>
/// There is intentionally no underwriter field. Who decided comes from the
/// authenticated principal, so it cannot be spoofed by the caller.
/// </remarks>
public sealed record ReviewDecisionRequest
{
    /// <summary><c>Approve</c>, <c>FollowUp</c> or <c>Decline</c>.</summary>
    /// <example>FollowUp</example>
    [Required]
    public string Decision { get; init; } = string.Empty;

    /// <summary>
    /// Required for <c>FollowUp</c> (what is needed) and <c>Decline</c> (why).
    /// Optional for <c>Approve</c>.
    /// </summary>
    /// <example>Upload proof of prior insurance from the last carrier.</example>
    [StringLength(500)]
    public string? Note { get; init; }
}
