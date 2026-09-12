using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for the quote endpoints, against real SQL Server.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class QuoteApiTests
{
    private readonly PolicyFlowApiFactory _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public QuoteApiTests(PolicyFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static object ValidQuoteRequest(
        string customerName = "Alex Johnson",
        string state = "GA",
        int vehicleYear = 2023,
        int annualMileage = 12_000,
        int priorInsuranceMonths = 36) => new
        {
            customerName,
            state,
            vehicleYear,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage,
            priorInsuranceMonths
        };

    [Fact]
    public async Task PostQuote_Returns202Accepted()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());

        // 202, not 201: the resource exists but its price does not yet.
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
    }

    [Fact]
    public async Task PostQuote_SetsTheLocationHeaderToTheStatusResource()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = body.GetProperty("quoteId").GetGuid();

        Assert.NotNull(response.Headers.Location);
        Assert.Contains(quoteId.ToString(), response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task PostQuote_ReturnsPendingWithAQuoteNumber()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.StartsWith("Q-2026-", body.GetProperty("quoteNumber").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task PostQuote_PersistsTheQuoteAndOutboxRowTogether()
    {
        // The transactional-outbox invariant, verified in real SQL: both rows
        // exist after one request, and the event is unsent.
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = body.GetProperty("quoteId").GetGuid();

        await _factory.WithDbAsync(async db =>
        {
            var quote = await db.Quotes.SingleOrDefaultAsync(q => q.Id == quoteId);
            Assert.NotNull(quote);
            Assert.Equal(QuoteStatus.Pending, quote!.Status);

            var outbox = await db.OutboxMessages
                .Where(m => m.Payload.Contains(quoteId.ToString()))
                .ToListAsync();

            var message = Assert.Single(outbox);
            Assert.Equal("QuoteSubmitted", message.EventType);
            // Unsent: Service Bus is not configured in this suite, so the row
            // correctly stays pending rather than being marked processed.
            Assert.Null(message.ProcessedUtc);
        });
    }

    [Fact]
    public async Task PostQuote_StampsARowVersionForOptimisticConcurrency()
    {
        // rowversion is a SQL Server feature with no in-memory equivalent, so this
        // can only be verified here.
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var quoteId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.WithDbAsync(async db =>
        {
            var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId);
            Assert.NotNull(quote.RowVersion);
            Assert.NotEmpty(quote.RowVersion!);
        });
    }

    [Fact]
    public async Task GetQuote_ReturnsTheCurrentStatus()
    {
        var created = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        var response = await _client.GetAsync($"/api/quotes/{quoteId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("isTerminal").GetBoolean());
    }

    [Fact]
    public async Task GetQuote_ReturnsProblemDetailsFor404()
    {
        var response = await _client.GetAsync($"/api/quotes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("Quote not found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnderwrittenQuote_BecomesRatedWithTheFullPanel()
    {
        var created = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);

        var response = await _client.GetAsync($"/api/quotes/{quoteId}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Rated", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("isTerminal").GetBoolean());
        Assert.Equal(15, body.GetProperty("offerCount").GetInt32());
        Assert.True(body.GetProperty("annualPremium").GetDecimal() > 0);
    }

    [Fact]
    public async Task RatedQuote_PersistsFifteenOffersWithNoDuplicates()
    {
        // UX_CarrierOffers_Quote_Carrier_Tier would reject a duplicate
        // carrier/tier row, so reaching 15 proves the fan-out wrote each exactly
        // once.
        var created = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);

        await _factory.WithDbAsync(async db =>
        {
            var offers = await db.CarrierOffers.Where(o => o.QuoteId == quoteId).ToListAsync();

            Assert.Equal(15, offers.Count);
            Assert.Equal(5, offers.Select(o => o.CarrierId).Distinct().Count());
            Assert.Equal(
                offers.Count,
                offers.Select(o => (o.CarrierId, o.CoverageTier)).Distinct().Count());
        });
    }

    [Fact]
    public async Task GetOffers_ReturnsTheComparison()
    {
        var created = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);

        var response = await _client.GetAsync($"/api/quotes/{quoteId}/offers");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(15, body.GetProperty("offers").GetArrayLength());

        var offers = body.GetProperty("offers").EnumerateArray().ToList();
        // Cheapest first, and exactly one best-price badge.
        Assert.True(
            offers[0].GetProperty("annualPremium").GetDecimal()
            <= offers[1].GetProperty("annualPremium").GetDecimal());
        Assert.Single(offers, o => o.GetProperty("isBestPrice").GetBoolean());
    }

    [Fact]
    public async Task NoPriorInsurance_IsReferredWithNoOffers()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/quotes", ValidQuoteRequest(priorInsuranceMonths: 0));
        var quoteId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(quoteId);

        var body = await (await _client.GetAsync($"/api/quotes/{quoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Referral", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("offerCount").GetInt32());
        Assert.False(body.TryGetProperty("annualPremium", out _));
    }

    // =====================================================================
    // Validation
    // =====================================================================

    [Fact]
    public async Task InvalidQuote_Returns400WithPerFieldErrors()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName = "",
            state = "ZZ",
            vehicleYear = 1850,
            vehicleMake = "",
            vehicleModel = "",
            annualMileage = -5,
            priorInsuranceMonths = -1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.True(errors.EnumerateObject().Any());
    }

    [Fact]
    public async Task UnsupportedState_IsRejected()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest(state: "CA"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidRequest_CreatesNothing()
    {
        var before = await _factory.WithDbAsync(db => db.Quotes.CountAsync());

        await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest(customerName: ""));

        var after = await _factory.WithDbAsync(db => db.Quotes.CountAsync());
        Assert.Equal(before, after);
    }

    // =====================================================================
    // Correlation id
    // =====================================================================

    [Fact]
    public async Task SuppliedCorrelationId_IsHonouredAndEchoed()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(ValidQuoteRequest())
        };
        request.Headers.Add("X-Correlation-Id", "my-trace-id-123");

        var response = await _client.SendAsync(request);

        Assert.Equal("my-trace-id-123", response.Headers.GetValues("X-Correlation-Id").Single());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("my-trace-id-123", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task SuppliedCorrelationId_ReachesTheDatabaseAndTheOutboxRow()
    {
        // This is what makes an end-to-end trace possible: the same id on the HTTP
        // response, the quote row and the message that will reach the worker.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(ValidQuoteRequest())
        };
        request.Headers.Add("X-Correlation-Id", "trace-through-sql");

        var response = await _client.SendAsync(request);
        var quoteId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.WithDbAsync(async db =>
        {
            var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId);
            Assert.Equal("trace-through-sql", quote.CorrelationId);

            var message = await db.OutboxMessages
                .SingleAsync(m => m.Payload.Contains(quoteId.ToString()));
            Assert.Equal("trace-through-sql", message.CorrelationId);
        });
    }

    [Fact]
    public async Task MissingCorrelationId_IsGenerated()
    {
        var response = await _client.PostAsJsonAsync("/api/quotes", ValidQuoteRequest());

        var correlationId = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(Guid.TryParse(correlationId, out _));
    }

    [Fact]
    public async Task CorrelationIdContainingANewline_IsReplaced()
    {
        // Log-injection defence: an unvalidated id would let a caller forge log
        // lines. HttpClient rejects a literal newline in a header, so the encoded
        // form is used here — which is what would actually arrive over the wire.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(ValidQuoteRequest())
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "evil%0d%0aFAKE-LOG-LINE");

        var response = await _client.SendAsync(request);

        var returned = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.DoesNotContain("FAKE-LOG-LINE", returned);
        Assert.True(Guid.TryParse(returned, out _));
    }

    [Fact]
    public async Task OverlongCorrelationId_IsReplaced()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Content = JsonContent.Create(ValidQuoteRequest())
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", new string('a', 500));

        var response = await _client.SendAsync(request);

        var returned = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(returned.Length <= 64);
    }

    // =====================================================================
    // Mobile self-service lookup
    // =====================================================================

    [Fact]
    public async Task Lookup_FindsAQuoteByNumberAndSurname()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/quotes", ValidQuoteRequest(customerName: "Dana Reyes"));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var quoteNumber = body.GetProperty("quoteNumber").GetString();

        var response = await _client.GetAsync(
            $"/api/quotes/lookup?quoteNumber={quoteNumber}&lastName=Reyes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(quoteNumber, result.GetProperty("quote").GetProperty("quoteNumber").GetString());
    }

    [Fact]
    public async Task Lookup_ReturnsTheSame404ForAWrongSurname()
    {
        // Identical response to an unknown quote number, so the endpoint cannot be
        // used to confirm which quote numbers exist.
        var created = await _client.PostAsJsonAsync(
            "/api/quotes", ValidQuoteRequest(customerName: "Dana Reyes"));
        var quoteNumber = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteNumber").GetString();

        var wrongName = await _client.GetAsync(
            $"/api/quotes/lookup?quoteNumber={quoteNumber}&lastName=Smith");
        var unknownNumber = await _client.GetAsync(
            "/api/quotes/lookup?quoteNumber=Q-2026-999999&lastName=Smith");

        Assert.Equal(HttpStatusCode.NotFound, wrongName.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownNumber.StatusCode);

        var wrongNameBody = await wrongName.Content.ReadAsStringAsync();
        var unknownBody = await unknownNumber.Content.ReadAsStringAsync();
        // Same title and detail: nothing distinguishes the two cases.
        Assert.Equal(
            JsonDocument.Parse(wrongNameBody).RootElement.GetProperty("detail").GetString(),
            JsonDocument.Parse(unknownBody).RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Lookup_ReturnsTheComparisonForARatedQuote()
    {
        // One round trip returns status and all fifteen offers, which is the point
        // for a phone on a slow connection.
        var created = await _client.PostAsJsonAsync(
            "/api/quotes", ValidQuoteRequest(customerName: "Sam Carter"));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = body.GetProperty("quoteId").GetGuid();
        var quoteNumber = body.GetProperty("quoteNumber").GetString();

        await _factory.UnderwriteAsync(quoteId);

        var response = await _client.GetAsync(
            $"/api/quotes/lookup?quoteNumber={quoteNumber}&lastName=Carter");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Rated", result.GetProperty("quote").GetProperty("status").GetString());
        Assert.Equal(15, result.GetProperty("comparison").GetProperty("offers").GetArrayLength());
    }

    [Theory]
    [InlineData("?quoteNumber=Q-2026-000001")]
    [InlineData("?lastName=Johnson")]
    [InlineData("")]
    public async Task Lookup_RequiresBothParameters(string query)
    {
        var response = await _client.GetAsync($"/api/quotes/lookup{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =====================================================================
    // Carrier catalog
    // =====================================================================

    [Fact]
    public async Task GetCarriers_ReturnsFiveCarriers()
    {
        var response = await _client.GetAsync("/api/carriers");
        var carriers = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, carriers.GetArrayLength());
    }

    [Fact]
    public async Task GetCarriers_DoesNotLeakInternalRatingFactors()
    {
        // The public carrier view must not expose base premiums, state factors or
        // the simulated latency bands: that is internal configuration.
        var carriers = await (await _client.GetAsync("/api/carriers"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var first = carriers.EnumerateArray().First();
        Assert.False(first.TryGetProperty("baseAnnualPremium", out _));
        Assert.False(first.TryGetProperty("stateFactors", out _));
        Assert.False(first.TryGetProperty("simulatedLatencyMsMin", out _));
    }

    [Fact]
    public async Task GetCarrier_Returns404ForAnUnknownId()
    {
        var response = await _client.GetAsync("/api/carriers/not-a-real-carrier");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // =====================================================================
    // Health and security headers
    // =====================================================================

    [Fact]
    public async Task HealthLive_IsHealthyWithoutCheckingDependencies()
    {
        // Liveness must not depend on SQL or the queue: a liveness probe that
        // checks the database restarts a healthy container during a blip.
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_ReportsEachDependency()
    {
        var response = await _client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        // Unhealthy overall is expected here: Service Bus is deliberately not
        // configured in this suite. The point is that each check is reported.
        Assert.Contains("sql-server", body);
        Assert.Contains("service-bus", body);
    }

    [Fact]
    public async Task Responses_CarryTheDefensiveSecurityHeaders()
    {
        var response = await _client.GetAsync("/api/carriers");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task ErrorResponses_DoNotLeakStackTraces()
    {
        // A stack trace in a client response maps out the internals for an
        // attacker. ProblemDetails carries a message and a correlation id only.
        var response = await _client.GetAsync($"/api/quotes/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("at PolicyFlow.", body);
        Assert.DoesNotContain("StackTrace", body);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", body);
    }
}
