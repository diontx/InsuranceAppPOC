using PolicyFlow.Application.Abstractions;

namespace PolicyFlow.Application.Policies;

/// <summary>Reads a policy with its payment history.</summary>
public sealed class GetPolicyHandler(IPolicyRepository policies, IQuoteRepository quotes)
{
    public async Task<PolicyView?> HandleAsync(Guid policyId, CancellationToken ct = default)
    {
        var policy = await policies.GetWithPaymentsAsync(policyId, ct);
        if (policy is null)
        {
            return null;
        }

        // The quote number is denormalised into the view so the UI can link back
        // to the comparison screen without a second round trip.
        var quote = await quotes.GetByIdAsync(policy.QuoteId, ct);
        return PolicyView.From(policy, quote?.QuoteNumber ?? string.Empty);
    }

    public async Task<PolicyView?> GetByPolicyNumberAsync(string policyNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(policyNumber))
        {
            return null;
        }

        var policy = await policies.GetByPolicyNumberAsync(policyNumber.Trim(), ct);
        return policy is null ? null : await HandleAsync(policy.Id, ct);
    }
}
