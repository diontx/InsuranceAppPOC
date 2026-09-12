using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Payments;
using PolicyFlow.Domain.Policies;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Domain.Tests;

/// <summary>Policy binding and payment recording rules.</summary>
public class PolicyAndPaymentTests
{
    private static readonly DateTime Now = TestQuotes.Now;
    private static readonly DateOnly Effective = new(2026, 7, 1);

    private static Quote RatedQuote(decimal premium = 1_200m)
    {
        var quote = TestQuotes.Clean();
        quote.MarkRated([TestQuotes.Offer(annualPremium: premium)], Now);
        return quote;
    }

    private static Policy BoundPolicy(decimal premium = 1_200m)
    {
        var quote = RatedQuote(premium);
        return Policy.CreateFromQuote(
            quote, quote.Offers.First(), "POL-2026-000001", Effective, "corr-1", Now);
    }

    [Fact]
    public void RatedQuote_BindsToATwelveMonthPolicy()
    {
        var quote = RatedQuote(1_482.50m);
        var offer = quote.Offers.First();

        var policy = Policy.CreateFromQuote(quote, offer, "POL-2026-000001", Effective, "corr-1", Now);

        Assert.Equal(PolicyStatus.Active, policy.Status);
        Assert.Equal(quote.Id, policy.QuoteId);
        Assert.Equal(offer.Id, policy.CarrierOfferId);
        Assert.Equal(1_482.50m, policy.AnnualPremium);
        Assert.Equal(Effective, policy.EffectiveDate);
        Assert.Equal(Effective.AddYears(1), policy.ExpirationDate);
    }

    [Fact]
    public void BoundPolicy_CopiesCarrierAndCoverageFromTheSelectedOffer()
    {
        var quote = RatedQuote();
        var offer = quote.Offers.First();

        var policy = Policy.CreateFromQuote(quote, offer, "POL-2026-000002", Effective, "corr-1", Now);

        Assert.Equal(offer.CarrierId, policy.CarrierId);
        Assert.Equal(offer.CarrierName, policy.CarrierName);
        Assert.Equal(offer.CoverageTier, policy.CoverageTier);
        Assert.Equal(offer.Deductible, policy.Deductible);
        Assert.Equal(offer.MonthlyPremium, policy.MonthlyPremium);
    }

    [Fact]
    public void PendingQuote_CannotBind()
    {
        var quote = TestQuotes.Clean();
        var orphanOffer = TestQuotes.Offer();

        var ex = Assert.Throws<DomainException>(() =>
            Policy.CreateFromQuote(quote, orphanOffer, "POL-2026-000003", Effective, "corr-1", Now));
        Assert.Contains("Only a Rated quote", ex.Message);
    }

    [Fact]
    public void ReferredQuote_CannotBind()
    {
        var quote = TestQuotes.Clean();
        quote.MarkReferral("Manual review.", Now);

        Assert.Throws<DomainException>(() =>
            Policy.CreateFromQuote(quote, TestQuotes.Offer(), "POL-2026-000004", Effective, "corr-1", Now));
    }

    [Fact]
    public void OfferFromAnotherQuote_CannotBind()
    {
        var quote = RatedQuote();

        Assert.Throws<DomainException>(() =>
            Policy.CreateFromQuote(quote, TestQuotes.Offer(), "POL-2026-000005", Effective, "corr-1", Now));
    }

    [Fact]
    public void BlankPolicyNumber_IsRejected()
    {
        var quote = RatedQuote();

        Assert.Throws<DomainException>(() =>
            Policy.CreateFromQuote(quote, quote.Offers.First(), "  ", Effective, "corr-1", Now));
    }

    [Fact]
    public void ActivePolicy_AcceptsAPayment()
    {
        var policy = BoundPolicy();

        var payment = policy.RecordPayment(125.00m, "idem-key-1", "TXN-SIM-0001", "corr-1", Now);

        Assert.Equal(PaymentStatus.Approved, payment.Status);
        Assert.Equal(125.00m, payment.Amount);
        Assert.Equal("idem-key-1", payment.IdempotencyKey);
        Assert.Equal(policy.Id, payment.PolicyId);
        Assert.Equal(125.00m, policy.TotalPaid);
    }

    [Fact]
    public void TotalPaid_SumsApprovedPayments()
    {
        var policy = BoundPolicy();

        policy.RecordPayment(100m, "k1", "TXN-1", "corr-1", Now);
        policy.RecordPayment(50m, "k2", "TXN-2", "corr-1", Now);

        Assert.Equal(150m, policy.TotalPaid);
        Assert.Equal(2, policy.Payments.Count);
    }

    [Fact]
    public void CancelledPolicy_RejectsPayments()
    {
        var policy = BoundPolicy();
        policy.Cancel(Now);

        var ex = Assert.Throws<DomainException>(() =>
            policy.RecordPayment(125m, "idem-key-2", "TXN-2", "corr-1", Now));
        Assert.Contains("Active policy", ex.Message);
    }

    [Fact]
    public void CancellingTwice_IsRejected()
    {
        var policy = BoundPolicy();
        policy.Cancel(Now);

        Assert.Throws<DomainException>(() => policy.Cancel(Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-500)]
    public void NonPositivePaymentAmount_IsRejected(decimal amount)
    {
        var policy = BoundPolicy();

        Assert.Throws<DomainException>(() =>
            policy.RecordPayment(amount, "idem-key-3", "TXN-3", "corr-1", Now));
    }

    [Fact]
    public void PaymentAboveTheDemoLimit_IsRejected()
    {
        var policy = BoundPolicy();

        Assert.Throws<DomainException>(() =>
            policy.RecordPayment(100_001m, "idem-key-4", "TXN-4", "corr-1", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingIdempotencyKey_IsRejected(string key)
    {
        // The API returns 400 for a missing header; the domain refuses it too, so
        // no code path can create an unkeyed payment.
        var policy = BoundPolicy();

        var ex = Assert.Throws<DomainException>(() =>
            policy.RecordPayment(125m, key, "TXN-5", "corr-1", Now));
        Assert.Contains("Idempotency-Key", ex.Message);
    }

    [Fact]
    public void PaymentAmount_IsRoundedToCents()
    {
        var policy = BoundPolicy();

        var payment = policy.RecordPayment(125.005m, "idem-key-6", "TXN-6", "corr-1", Now);

        Assert.Equal(125.01m, payment.Amount);
    }

    [Fact]
    public void IdempotencyKey_IsTrimmed()
    {
        // A trailing newline from a curl script must not create a distinct key.
        var policy = BoundPolicy();

        var payment = policy.RecordPayment(125m, "  idem-key-7  ", "TXN-7", "corr-1", Now);

        Assert.Equal("idem-key-7", payment.IdempotencyKey);
    }
}
