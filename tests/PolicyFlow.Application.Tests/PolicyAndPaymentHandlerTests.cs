using Microsoft.EntityFrameworkCore;
using PolicyFlow.Application.Payments;
using PolicyFlow.Application.Policies;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// Policy binding and premium payment.
/// </summary>
/// <remarks>
/// These cover the application-level behaviour. The database-enforced guarantees
/// (the UNIQUE indexes behind idempotency and duplicate-bind prevention) are
/// verified against real SQL Server in <c>PolicyFlow.Api.IntegrationTests</c>,
/// because the in-memory provider has no constraints to violate.
/// </remarks>
public class PolicyAndPaymentHandlerTests
{
    [Fact]
    public async Task RatedQuote_BindsToAPolicy()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id });

        Assert.Equal("Active", policy.Status);
        Assert.Equal("POL-2026-000001", policy.PolicyNumber);
        Assert.Equal(quote.QuoteNumber, policy.QuoteNumber);
        Assert.Equal(quote.AnnualPremium, policy.AnnualPremium);
    }

    [Fact]
    public async Task BindingWithoutAnOfferId_TakesTheCheapestOption()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id });

        var cheapest = quote.Offers.OrderBy(o => o.AnnualPremium).First();
        Assert.Equal(cheapest.CarrierId, policy.CarrierId);
        Assert.Equal(cheapest.AnnualPremium, policy.AnnualPremium);
    }

    [Fact]
    public async Task BindingWithAnOfferId_HonoursTheCustomerChoice()
    {
        // The customer may knowingly pay more for better coverage. The bound
        // premium must be the one they were shown, not the cheapest.
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();
        var chosen = quote.Offers
            .Where(o => o.CoverageTier == CoverageTier.Premium)
            .OrderByDescending(o => o.AnnualPremium)
            .First();

        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id, OfferId = chosen.Id });

        Assert.Equal(chosen.CarrierId, policy.CarrierId);
        Assert.Equal(chosen.AnnualPremium, policy.AnnualPremium);
        Assert.Equal("Premium", policy.CoverageTier);
    }

    [Fact]
    public async Task PendingQuote_CannotBind()
    {
        await using var harness = new TestHarness();
        var submitted = await harness.SubmitQuote.HandleAsync(new Quotes.SubmitQuoteCommand
        {
            CustomerName = "Alex Johnson",
            State = "GA",
            VehicleYear = 2023,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = 12_000,
            PriorInsuranceMonths = 36
        });

        // Underwriting has not run, so the quote is still Pending.
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => harness.CreatePolicy.HandleAsync(
                new CreatePolicyFromQuoteCommand { QuoteId = submitted.QuoteId }));

        Assert.Contains("Rated", ex.Message);
    }

    [Fact]
    public async Task ReferredQuote_CannotBind()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync(priorInsuranceMonths: 0);

        await Assert.ThrowsAsync<DomainException>(
            () => harness.CreatePolicy.HandleAsync(
                new CreatePolicyFromQuoteCommand { QuoteId = quote.Id }));
    }

    [Fact]
    public async Task DuplicateBinding_IsRejectedAsAConflict()
    {
        // The application-level guard. The UNIQUE index on Policy.QuoteId is the
        // real guarantee and is exercised in the integration tests.
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id });

        var ex = await Assert.ThrowsAsync<BusinessConflictException>(
            () => harness.CreatePolicy.HandleAsync(
                new CreatePolicyFromQuoteCommand { QuoteId = quote.Id }));

        Assert.Contains("already bound", ex.Message);
        Assert.Equal(1, await harness.Db.Policies.CountAsync());
    }

    [Fact]
    public async Task BindingAnUnknownQuote_ReportsNotFound()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<QuoteNotFoundException>(
            () => harness.CreatePolicy.HandleAsync(
                new CreatePolicyFromQuoteCommand { QuoteId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task BindingAnOfferFromAnotherQuote_IsRejected()
    {
        await using var harness = new TestHarness();
        var first = await harness.SubmitAndUnderwriteAsync();
        var second = await harness.SubmitAndUnderwriteAsync();

        await Assert.ThrowsAsync<DomainException>(
            () => harness.CreatePolicy.HandleAsync(new CreatePolicyFromQuoteCommand
            {
                QuoteId = first.Id,
                OfferId = second.Offers.First().Id
            }));
    }

    [Fact]
    public async Task PolicyTerm_DefaultsToTwelveMonthsFromTomorrow()
    {
        await using var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();

        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id });

        var expectedEffective = DateOnly.FromDateTime(harness.Clock.UtcNow.Date).AddDays(1);
        Assert.Equal(expectedEffective, policy.EffectiveDate);
        Assert.Equal(expectedEffective.AddYears(1), policy.ExpirationDate);
    }

    [Fact]
    public async Task PolicyNumbers_AreSequential()
    {
        await using var harness = new TestHarness();
        var first = await harness.SubmitAndUnderwriteAsync();
        var second = await harness.SubmitAndUnderwriteAsync();

        var policyA = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = first.Id });
        var policyB = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = second.Id });

        Assert.Equal("POL-2026-000001", policyA.PolicyNumber);
        Assert.Equal("POL-2026-000002", policyB.PolicyNumber);
    }

    // =====================================================================
    // Payment idempotency
    // =====================================================================

    private async Task<(TestHarness Harness, PolicyView Policy)> BoundPolicyAsync()
    {
        var harness = new TestHarness();
        var quote = await harness.SubmitAndUnderwriteAsync();
        var policy = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quote.Id });
        return (harness, policy);
    }

    [Fact]
    public async Task Payment_IsRecordedAgainstThePolicy()
    {
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        var payment = await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId,
            Amount = 125.00m,
            IdempotencyKey = "key-1"
        });

        Assert.Equal("Approved", payment.Status);
        Assert.Equal(125.00m, payment.Amount);
        Assert.False(payment.WasReplayed);
        Assert.StartsWith("TXN-SIM-", payment.ExternalTransactionId);
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsTheOriginalTransaction()
    {
        // The headline behaviour. A retried payment must return the original and
        // create nothing.
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        var command = new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId,
            Amount = 125.00m,
            IdempotencyKey = "retry-me"
        };

        var first = await harness.SubmitPayment.HandleAsync(command);
        var second = await harness.SubmitPayment.HandleAsync(command);

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal(first.ExternalTransactionId, second.ExternalTransactionId);
        Assert.False(first.WasReplayed);
        Assert.True(second.WasReplayed);

        // One logical transaction, however many requests arrived.
        Assert.Equal(1, await harness.Db.Payments.CountAsync());
    }

    [Fact]
    public async Task RepeatedSubmissions_StillProduceOnePayment()
    {
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        var command = new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId,
            Amount = 125.00m,
            IdempotencyKey = "hammer-it"
        };

        for (var i = 0; i < 5; i++)
        {
            await harness.SubmitPayment.HandleAsync(command);
        }

        Assert.Equal(1, await harness.Db.Payments.CountAsync());
        Assert.Equal(125.00m, await harness.Db.Payments.SumAsync(p => p.Amount));
    }

    [Fact]
    public async Task DifferentIdempotencyKeys_CreateSeparatePayments()
    {
        // Two genuinely different payments must both be recorded. Over-eager
        // deduplication would lose a real instalment.
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 125.00m, IdempotencyKey = "month-1"
        });
        await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 125.00m, IdempotencyKey = "month-2"
        });

        Assert.Equal(2, await harness.Db.Payments.CountAsync());
        Assert.Equal(250.00m, await harness.Db.Payments.SumAsync(p => p.Amount));
    }

    [Fact]
    public async Task ReplayIgnoresAChangedAmount_AndReturnsTheOriginal()
    {
        // A client that retries with the same key but a different amount is
        // confused. The key identifies the intent, so the original stands: the
        // alternative is silently charging an amount nobody confirmed.
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        var first = await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 125.00m, IdempotencyKey = "same-key"
        });

        var second = await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 999.00m, IdempotencyKey = "same-key"
        });

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal(125.00m, second.Amount);
        Assert.True(second.WasReplayed);
        Assert.Equal(1, await harness.Db.Payments.CountAsync());
    }

    [Fact]
    public async Task IdempotencyKeyIsScopedToThePolicy()
    {
        // Keys are client-chosen, so two customers can legitimately pick the same
        // one. Scoping to the policy means neither payment is lost.
        await using var harness = new TestHarness();

        var quoteA = await harness.SubmitAndUnderwriteAsync();
        var quoteB = await harness.SubmitAndUnderwriteAsync();
        var policyA = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quoteA.Id });
        var policyB = await harness.CreatePolicy.HandleAsync(
            new CreatePolicyFromQuoteCommand { QuoteId = quoteB.Id });

        await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policyA.PolicyId, Amount = 100m, IdempotencyKey = "collision"
        });
        var second = await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policyB.PolicyId, Amount = 200m, IdempotencyKey = "collision"
        });

        Assert.False(second.WasReplayed);
        Assert.Equal(2, await harness.Db.Payments.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingIdempotencyKey_IsRejected(string key)
    {
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        await Assert.ThrowsAsync<MissingIdempotencyKeyException>(
            () => harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
            {
                PolicyId = policy.PolicyId, Amount = 125m, IdempotencyKey = key
            }));

        Assert.Equal(0, await harness.Db.Payments.CountAsync());
    }

    [Fact]
    public async Task WhitespaceAroundTheKey_IsTreatedAsTheSameKey()
    {
        // A trailing newline from a curl script must not look like a new payment.
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 125m, IdempotencyKey = "trim-me"
        });
        var second = await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 125m, IdempotencyKey = "  trim-me  "
        });

        Assert.True(second.WasReplayed);
        Assert.Equal(1, await harness.Db.Payments.CountAsync());
    }

    [Fact]
    public async Task PaymentAgainstAnUnknownPolicy_ReportsNotFound()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<PolicyNotFoundException>(
            () => harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
            {
                PolicyId = Guid.NewGuid(), Amount = 125m, IdempotencyKey = "key"
            }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task NonPositiveAmount_IsRejected(decimal amount)
    {
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        await Assert.ThrowsAsync<DomainException>(
            () => harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
            {
                PolicyId = policy.PolicyId, Amount = amount, IdempotencyKey = "bad-amount"
            }));
    }

    [Fact]
    public async Task PolicyView_ReportsPaidAndBalance()
    {
        var (harness, policy) = await BoundPolicyAsync();
        await using var _ = harness;

        await harness.SubmitPayment.HandleAsync(new SubmitPaymentCommand
        {
            PolicyId = policy.PolicyId, Amount = 300m, IdempotencyKey = "part-payment"
        });

        harness.Db.ChangeTracker.Clear();
        var reloaded = await harness.GetPolicy.HandleAsync(policy.PolicyId);

        Assert.Equal(300m, reloaded!.TotalPaid);
        Assert.Equal(decimal.Round(policy.AnnualPremium - 300m, 2), reloaded.BalanceDue);
        Assert.Single(reloaded.Payments);
    }

    [Fact]
    public async Task GetPolicy_ReturnsNullForAnUnknownId()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetPolicy.HandleAsync(Guid.NewGuid()));
    }
}
