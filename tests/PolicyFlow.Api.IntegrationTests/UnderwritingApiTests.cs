using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PolicyFlow.Api.Controllers;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// The underwriting workbench API over HTTP, against real SQL Server.
/// </summary>
/// <remarks>
/// Real SQL Server matters for two of these: the review columns must exist in the
/// schema the migrations actually produce, and concurrent decisions on one quote
/// are resolved by the <c>rowversion</c> on <c>Quotes</c>, which the in-memory
/// provider does not have.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class UnderwritingApiTests
{
    private readonly PolicyFlowApiFactory _factory;
    private readonly HttpClient _client;

    public UnderwritingApiTests(PolicyFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Submits a quote the house rules refer, and underwrites it.</summary>
    private async Task<(Guid QuoteId, string QuoteNumber)> SubmitReferralAsync(
        string customerName = "Riley Morgan",
        int priorInsuranceMonths = 0,
        int annualMileage = 12_000)
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName,
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Honda",
            vehicleModel = "Civic",
            annualMileage,
            priorInsuranceMonths
        });
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = body.GetProperty("quoteId").GetGuid();
        await _factory.UnderwriteAsync(quoteId);

        return (quoteId, body.GetProperty("quoteNumber").GetString()!);
    }

    private Task<HttpResponseMessage> DecideAsync(Guid quoteId, string decision, string? note = null) =>
        _client.PostAsJsonAsync($"/api/underwriting/quotes/{quoteId}/decision", new { decision, note });

    private async Task<JsonElement> QueueAsync()
    {
        var response = await _client.GetAsync("/api/underwriting/queue");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static bool QueueContains(JsonElement queue, Guid quoteId) =>
        queue.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("quoteId").GetGuid() == quoteId);

    // =====================================================================
    // Queue and review
    // =====================================================================

    [Fact]
    public async Task Queue_ListsAReferral_AndNotARatedQuote()
    {
        var (referralId, _) = await SubmitReferralAsync();
        var (ratedId, _) = await SubmitReferralAsync(priorInsuranceMonths: 36);

        var queue = await QueueAsync();

        Assert.True(QueueContains(queue, referralId));
        Assert.False(QueueContains(queue, ratedId));

        var item = queue.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("quoteId").GetGuid() == referralId);
        Assert.Equal("Referral", item.GetProperty("status").GetString());
        Assert.Equal("2023 Honda Civic", item.GetProperty("vehicleDescription").GetString());
    }

    [Fact]
    public async Task Review_ReturnsTheApplicationTheTraceAndCarrierAppetite()
    {
        var (quoteId, quoteNumber) = await SubmitReferralAsync(customerName: "Casey Alvarez");

        var response = await _client.GetAsync($"/api/underwriting/quotes/{quoteId}");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(quoteNumber, review.GetProperty("quoteNumber").GetString());
        Assert.Equal("Casey Alvarez", review.GetProperty("customerName").GetString());
        Assert.True(review.GetProperty("isAwaitingReview").GetBoolean());
        Assert.Equal(0, review.GetProperty("priorInsuranceMonths").GetInt32());
        Assert.Equal("Refer", review.GetProperty("eligibilityOutcome").GetString());
        Assert.NotEmpty(review.GetProperty("eligibilityTrace").EnumerateArray());
        Assert.Equal(3, review.GetProperty("carrierAppetite").EnumerateArray()
            .Count(c => c.GetProperty("willWrite").GetBoolean()));
    }

    [Fact]
    public async Task Review_OfAnUnknownQuote_Returns404()
    {
        var response = await _client.GetAsync($"/api/underwriting/quotes/{Guid.NewGuid()}");

        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, response);
    }

    // =====================================================================
    // Decisions
    // =====================================================================

    [Fact]
    public async Task Approve_MakesTheQuoteRatedAndBindable_AndRemovesItFromTheQueue()
    {
        var (quoteId, _) = await SubmitReferralAsync();

        var response = await DecideAsync(quoteId, "Approve", "Lapse verified as a relocation.");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rated", review.GetProperty("status").GetString());
        Assert.Equal(9, review.GetProperty("offerCount").GetInt32());

        // The customer-facing status endpoint agrees...
        var status = await (await _client.GetAsync($"/api/quotes/{quoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rated", status.GetProperty("status").GetString());

        // ...an agent can bind it like any rated quote...
        var bind = await _client.PostAsJsonAsync($"/api/policies/from-quote/{quoteId}", new { });
        await HttpAssert.StatusAsync(HttpStatusCode.Created, bind);

        // ...and the underwriter's queue no longer shows it.
        Assert.False(QueueContains(await QueueAsync(), quoteId));
    }

    [Fact]
    public async Task FollowUp_ThenDecline_WalksTheQuoteThroughTheQueue()
    {
        var (quoteId, quoteNumber) = await SubmitReferralAsync();

        var followUp = await DecideAsync(quoteId, "FollowUp", "Upload proof of prior insurance.");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, followUp);

        var status = await (await _client.GetAsync($"/api/quotes/{quoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FollowUp", status.GetProperty("status").GetString());
        Assert.True(status.GetProperty("isTerminal").GetBoolean());
        Assert.Equal("Upload proof of prior insurance.", status.GetProperty("decisionReason").GetString());
        Assert.True(QueueContains(await QueueAsync(), quoteId));

        // The legacy status page sees the new status in its own format.
        var legacy = await (await _client.GetAsync($"/legacy/quotes/{quoteNumber}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FOLLOWUP", legacy.GetProperty("quoteStatus").GetString());

        var decline = await DecideAsync(quoteId, "decline", "No response after 30 days.");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, decline);
        Assert.Equal("Declined",
            (await decline.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.False(QueueContains(await QueueAsync(), quoteId));
    }

    [Fact]
    public async Task Decision_IsPersistedToTheReviewColumns()
    {
        // Proves the migration actually added the columns the aggregate writes.
        var (quoteId, _) = await SubmitReferralAsync();

        await DecideAsync(quoteId, "FollowUp", "Upload proof of prior insurance.");

        await _factory.WithDbAsync(async db =>
        {
            var quote = await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
            Assert.Equal(QuoteStatus.FollowUp, quote.Status);
            Assert.Equal(UnderwritingController.UnauthenticatedUnderwriter, quote.ReviewedBy);
            Assert.Equal("Upload proof of prior insurance.", quote.ReviewNote);
            Assert.NotNull(quote.ReviewedUtc);
        });
    }

    [Fact]
    public async Task Decision_RecordsTheUnderwriterFromTheToken_NotFromTheRequest()
    {
        var (quoteId, _) = await SubmitReferralAsync();

        var tokenResponse = await _client.PostAsJsonAsync(
            "/api/auth/dev-token", new { role = "Underwriter", subject = "j.okafor" });
        await HttpAssert.StatusAsync(HttpStatusCode.OK, tokenResponse);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/underwriting/quotes/{quoteId}/decision")
        {
            // An extra "underwriter" field is ignored: the contract has no such member.
            Content = JsonContent.Create(new
            {
                decision = "Decline",
                note = "Unable to verify any driving history.",
                underwriter = "someone-else"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);

        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("j.okafor", review.GetProperty("reviewedBy").GetString());
    }

    // =====================================================================
    // Failure paths
    // =====================================================================

    [Fact]
    public async Task FollowUpWithoutANote_Returns400WithAFieldError_AndChangesNothing()
    {
        var (quoteId, _) = await SubmitReferralAsync();

        var response = await DecideAsync(quoteId, "FollowUp", note: null);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("Note", out _));

        await _factory.WithDbAsync(async db =>
            Assert.Equal(QuoteStatus.Referral,
                (await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId)).Status));
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("1")]
    [InlineData("")]
    public async Task UnknownDecision_Returns400(string decision)
    {
        var (quoteId, _) = await SubmitReferralAsync();

        var response = await DecideAsync(quoteId, decision, "A perfectly good note.");

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task DecisionOnAnUnknownQuote_Returns404()
    {
        var response = await DecideAsync(Guid.NewGuid(), "Approve");

        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task DecidingTwice_Returns409()
    {
        var (quoteId, _) = await SubmitReferralAsync();
        await HttpAssert.StatusAsync(HttpStatusCode.OK,
            await DecideAsync(quoteId, "Decline", "Unable to verify any driving history."));

        var second = await DecideAsync(quoteId, "Approve");

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, second);
    }

    [Fact]
    public async Task ApprovingARiskNoCarrierWillWrite_Returns422_AndLeavesItInTheQueue()
    {
        // Mileage above every carrier's cap: referred by the panel, not the house rules.
        var (quoteId, _) = await SubmitReferralAsync(priorInsuranceMonths: 36, annualMileage: 95_000);

        var response = await DecideAsync(quoteId, "Approve");

        await HttpAssert.StatusAsync(HttpStatusCode.UnprocessableEntity, response);
        Assert.True(QueueContains(await QueueAsync(), quoteId));
    }

    [Fact]
    public async Task ConcurrentDecisionsOnOneQuote_ExactlyOneWins()
    {
        // Five underwriters press Decline at the same moment. The status pre-check
        // cannot stop requests that all read "Referral" before any commits; the
        // rowversion on Quotes does. Exactly one decision may be recorded.
        var (quoteId, _) = await SubmitReferralAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            DecideAsync(quoteId, "Decline", $"Concurrent decline number {i}.")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Conflict, loser);
        }
    }
}
