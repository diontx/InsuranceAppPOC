using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// Policy binding and payment idempotency against <b>real SQL Server</b>.
/// </summary>
/// <remarks>
/// This is the suite that actually proves the transaction-safety claims. The
/// guarantees under test are UNIQUE indexes, not application code:
/// <c>UX_Payments_PolicyId_IdempotencyKey</c> and <c>UX_Policies_QuoteId</c>.
/// Against an in-memory provider these tests would pass without the constraints
/// existing at all, which is why they live here.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class PolicyAndPaymentApiTests
{
    private readonly PolicyFlowApiFactory _factory;
    private readonly HttpClient _client;

    public PolicyAndPaymentApiTests(PolicyFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Submits and underwrites a quote, returning its id.</summary>
    private async Task<Guid> RatedQuoteAsync(string customerName = "Alex Johnson")
    {
        var created = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName,
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 12_000,
            priorInsuranceMonths = 36
        });

        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);
        return quoteId;
    }

    private async Task<Guid> BoundPolicyAsync(string customerName = "Alex Johnson")
    {
        var quoteId = await RatedQuoteAsync(customerName);
        var response = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("policyId").GetGuid();
    }

    /// <summary>Builds a payment request with an explicit idempotency key.</summary>
    private static HttpRequestMessage PaymentRequest(Guid policyId, decimal amount, string? key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/policies/{policyId}/payments")
        {
            Content = JsonContent.Create(new { amount })
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    // =====================================================================
    // Policy binding
    // =====================================================================

    [Fact]
    public async Task PostPolicyFromRatedQuote_Returns201Created()
    {
        var quoteId = await RatedQuoteAsync();

        var response = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.StartsWith("POL-2026-", body.GetProperty("policyNumber").GetString());
    }

    [Fact]
    public async Task PostPolicyFromPendingQuote_IsRejected()
    {
        // Not underwritten, so still Pending: binding must fail with 422, not
        // create a policy at an unpriced premium.
        var created = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName = "Unrated Person",
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 12_000,
            priorInsuranceMonths = 36
        });
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        var response = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Rated", problem.GetProperty("detail").GetString()!);

        await _factory.WithDbAsync(async db =>
            Assert.Equal(0, await db.Policies.CountAsync(p => p.QuoteId == quoteId)));
    }

    [Fact]
    public async Task PostPolicyFromReferredQuote_IsRejected()
    {
        var created = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName = "Referred Person",
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 12_000,
            priorInsuranceMonths = 0
        });
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);

        var response = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
    }

    [Fact]
    public async Task DuplicatePolicyBinding_Returns409AndCreatesOnePolicy()
    {
        // Backed by UX_Policies_QuoteId in real SQL Server.
        var quoteId = await RatedQuoteAsync();

        var first = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });
        var second = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await _factory.WithDbAsync(async db =>
            Assert.Equal(1, await db.Policies.CountAsync(p => p.QuoteId == quoteId)));
    }

    [Fact]
    public async Task ConcurrentPolicyBinding_StillCreatesOnlyOnePolicy()
    {
        // Ten simultaneous requests. The application pre-read cannot prevent this
        // on its own — several requests pass it before any insert commits — so
        // this is the UNIQUE index doing the work.
        var quoteId = await RatedQuoteAsync();

        var attempts = Enumerable.Range(0, 10)
            .Select(_ => _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { }))
            .ToArray();

        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        await _factory.WithDbAsync(async db =>
            Assert.Equal(1, await db.Policies.CountAsync(p => p.QuoteId == quoteId)));
    }

    [Fact]
    public async Task PostPolicyForUnknownQuote_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/policies/from-quote/{Guid.NewGuid()}", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BoundPolicy_CopiesTheSelectedOfferPremium()
    {
        var quoteId = await RatedQuoteAsync();

        var offers = await (await _client.GetAsync($"/api/quotes/{quoteId}/offers"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var chosen = offers.GetProperty("offers").EnumerateArray()
            .First(o => o.GetProperty("coverageTier").GetString() == "Premium");
        var chosenOfferId = chosen.GetProperty("offerId").GetGuid();
        var chosenPremium = chosen.GetProperty("annualPremium").GetDecimal();

        var response = await _client.PostAsJsonAsync(
            $"/api/policies/from-quote/{quoteId}", new { offerId = chosenOfferId });
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The premium that binds is the one the customer was shown, not the
        // cheapest on the panel.
        Assert.Equal(chosenPremium, policy.GetProperty("annualPremium").GetDecimal());
        Assert.Equal("Premium", policy.GetProperty("coverageTier").GetString());
    }

    [Fact]
    public async Task GetPolicy_ReturnsTheBoundPolicy()
    {
        var policyId = await BoundPolicyAsync();

        var response = await _client.GetAsync($"/api/policies/{policyId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(policyId, body.GetProperty("policyId").GetGuid());
        Assert.Equal(0m, body.GetProperty("totalPaid").GetDecimal());
    }

    [Fact]
    public async Task GetPolicy_Returns404ForAnUnknownId()
    {
        var response = await _client.GetAsync($"/api/policies/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Payment idempotency — the headline behaviour
    // =====================================================================

    [Fact]
    public async Task PaymentWithoutIdempotencyKey_Returns400()
    {
        var policyId = await BoundPolicyAsync();

        var response = await _client.SendAsync(PaymentRequest(policyId, 125m, key: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Idempotency-Key", problem.GetProperty("detail").GetString()!);

        // Nothing was recorded: an unkeyed payment cannot be made safe, so the API
        // refuses rather than guessing.
        await _factory.WithDbAsync(async db =>
            Assert.Equal(0, await db.Payments.CountAsync(p => p.PolicyId == policyId)));
    }

    [Fact]
    public async Task FirstPayment_Returns200AndIsRecorded()
    {
        var policyId = await BoundPolicyAsync();

        var response = await _client.SendAsync(PaymentRequest(policyId, 125m, "key-first"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Approved", body.GetProperty("status").GetString());
        Assert.Equal(125m, body.GetProperty("amount").GetDecimal());
        Assert.False(body.GetProperty("wasReplayed").GetBoolean());
    }

    [Fact]
    public async Task DuplicatePayment_ReturnsTheOriginalAndPersistsOneTransaction()
    {
        // THE test the demo script builds to. Same key twice, one database row.
        var policyId = await BoundPolicyAsync();

        var first = await _client.SendAsync(PaymentRequest(policyId, 125m, "duplicate-me"));
        var second = await _client.SendAsync(PaymentRequest(policyId, 125m, "duplicate-me"));

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Same status code on both, so a retrying client cannot tell the
        // difference between the original and the replay.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(
            firstBody.GetProperty("paymentId").GetGuid(),
            secondBody.GetProperty("paymentId").GetGuid());
        Assert.Equal(
            firstBody.GetProperty("externalTransactionId").GetString(),
            secondBody.GetProperty("externalTransactionId").GetString());

        Assert.False(firstBody.GetProperty("wasReplayed").GetBoolean());
        Assert.True(secondBody.GetProperty("wasReplayed").GetBoolean());

        await _factory.WithDbAsync(async db =>
        {
            var payments = await db.Payments.Where(p => p.PolicyId == policyId).ToListAsync();
            Assert.Single(payments);
            Assert.Equal(125m, payments[0].Amount);
        });
    }

    [Fact]
    public async Task ConcurrentPaymentsWithTheSameKey_CollapseToOneTransaction()
    {
        // The case application-level checking cannot handle. Twenty simultaneous
        // requests all pass the "does this key exist" read before any insert
        // commits; only the UNIQUE index stops nineteen duplicate charges.
        var policyId = await BoundPolicyAsync();

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => _client.SendAsync(PaymentRequest(policyId, 125m, "race-key")))
            .ToArray();

        var responses = await Task.WhenAll(attempts);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await _factory.WithDbAsync(async db =>
        {
            var payments = await db.Payments.Where(p => p.PolicyId == policyId).ToListAsync();

            // One row, and therefore one charge, from twenty concurrent requests.
            Assert.Single(payments);
            Assert.Equal(125m, payments.Sum(p => p.Amount));
        });

        // Every response describes the same transaction.
        var bodies = await Task.WhenAll(
            responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var paymentIds = bodies.Select(b => b.GetProperty("paymentId").GetGuid()).Distinct();
        Assert.Single(paymentIds);
    }

    [Fact]
    public async Task DifferentKeys_CreateSeparatePayments()
    {
        // Deduplication must not be over-eager: two real instalments must both
        // be recorded.
        var policyId = await BoundPolicyAsync();

        await _client.SendAsync(PaymentRequest(policyId, 125m, "instalment-1"));
        await _client.SendAsync(PaymentRequest(policyId, 125m, "instalment-2"));

        await _factory.WithDbAsync(async db =>
        {
            var payments = await db.Payments.Where(p => p.PolicyId == policyId).ToListAsync();
            Assert.Equal(2, payments.Count);
            Assert.Equal(250m, payments.Sum(p => p.Amount));
        });
    }

    [Fact]
    public async Task ReplayIgnoresAChangedAmount()
    {
        // A client retrying with the same key but a different amount is confused.
        // The key identifies the intent, so the original amount stands — charging
        // an amount nobody confirmed would be worse.
        var policyId = await BoundPolicyAsync();

        await _client.SendAsync(PaymentRequest(policyId, 125m, "same-key-new-amount"));
        var second = await _client.SendAsync(PaymentRequest(policyId, 5_000m, "same-key-new-amount"));

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(125m, body.GetProperty("amount").GetDecimal());
        Assert.True(body.GetProperty("wasReplayed").GetBoolean());

        await _factory.WithDbAsync(async db =>
            Assert.Equal(125m, await db.Payments
                .Where(p => p.PolicyId == policyId)
                .SumAsync(p => p.Amount)));
    }

    [Fact]
    public async Task IdempotencyKeysAreScopedPerPolicy()
    {
        // Keys are client-chosen, so two customers can pick the same one. A
        // globally unique index would reject a legitimate second payment, which is
        // why the constraint is (PolicyId, IdempotencyKey).
        var policyA = await BoundPolicyAsync("Alex Johnson");
        var policyB = await BoundPolicyAsync("Dana Reyes");

        var first = await _client.SendAsync(PaymentRequest(policyA, 100m, "shared-key"));
        var second = await _client.SendAsync(PaymentRequest(policyB, 200m, "shared-key"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(secondBody.GetProperty("wasReplayed").GetBoolean());

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Payments.CountAsync(p => p.PolicyId == policyA));
            Assert.Equal(1, await db.Payments.CountAsync(p => p.PolicyId == policyB));
        });
    }

    [Fact]
    public async Task PolicyBalance_ReflectsRecordedPayments()
    {
        var policyId = await BoundPolicyAsync();

        var before = await (await _client.GetAsync($"/api/policies/{policyId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var annualPremium = before.GetProperty("annualPremium").GetDecimal();

        await _client.SendAsync(PaymentRequest(policyId, 300m, "balance-check"));

        var after = await (await _client.GetAsync($"/api/policies/{policyId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(300m, after.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(
            decimal.Round(annualPremium - 300m, 2),
            after.GetProperty("balanceDue").GetDecimal());
        Assert.Equal(1, after.GetProperty("payments").GetArrayLength());
    }

    [Fact]
    public async Task RepeatedRetries_LeaveOneRowInThePaymentHistory()
    {
        // What the demo shows on screen: submit five times, see one payment.
        var policyId = await BoundPolicyAsync();

        for (var i = 0; i < 5; i++)
        {
            await _client.SendAsync(PaymentRequest(policyId, 125m, "five-retries"));
        }

        var policy = await (await _client.GetAsync($"/api/policies/{policyId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, policy.GetProperty("payments").GetArrayLength());
        Assert.Equal(125m, policy.GetProperty("totalPaid").GetDecimal());
    }

    [Fact]
    public async Task PaymentForUnknownPolicy_Returns404()
    {
        var response = await _client.SendAsync(PaymentRequest(Guid.NewGuid(), 125m, "orphan-key"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task NonPositivePaymentAmount_IsRejected(decimal amount)
    {
        var policyId = await BoundPolicyAsync();

        var response = await _client.SendAsync(PaymentRequest(policyId, amount, "bad-amount"));

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableContent,
            $"Expected 400 or 422 but got {(int)response.StatusCode}.");

        await _factory.WithDbAsync(async db =>
            Assert.Equal(0, await db.Payments.CountAsync(p => p.PolicyId == policyId)));
    }

    [Fact]
    public async Task PaymentResponse_DoesNotLeakTheIdempotencyKey()
    {
        // The key is a client-chosen value that could contain anything; it has no
        // business appearing in a response body or a log.
        var policyId = await BoundPolicyAsync();

        var response = await _client.SendAsync(
            PaymentRequest(policyId, 125m, "secret-looking-key-value"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("secret-looking-key-value", body);
    }
}
