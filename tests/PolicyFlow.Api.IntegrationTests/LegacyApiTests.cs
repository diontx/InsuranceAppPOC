using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// The legacy compatibility seam over HTTP.
/// </summary>
/// <remarks>
/// The property under test is that <c>POST /legacy/quotes</c> and
/// <c>POST /api/quotes</c> reach the same application logic and produce identical
/// business outcomes. The instruction document forbids duplicated rating logic,
/// and this is what proves the rule is actually held rather than merely intended.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class LegacyApiTests
{
    private readonly PolicyFlowApiFactory _factory;
    private readonly HttpClient _client;

    public LegacyApiTests(PolicyFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>The legacy wire format: PascalCase names, every value a string.</summary>
    private static object LegacyRequest(
        string insuredName = "Alex Johnson",
        string riskState = "GA",
        string vehYear = "2023",
        string mileage = "12000",
        string priorCovMonths = "36") => new
        {
            InsuredName = insuredName,
            RiskState = riskState,
            VehYear = vehYear,
            VehMake = "Toyota",
            VehModel = "Camry",
            Mileage = mileage,
            PriorCovMonths = priorCovMonths
        };

    [Fact]
    public async Task LegacyQuote_Returns200WithReturnCodeZero()
    {
        var response = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());

        // 200 with a return code, not 202. A deliberate concession: the legacy
        // client predates ProblemDetails and cannot interpret a 4xx body.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("returnCode").GetInt32());
        Assert.Equal("QUOTE ACCEPTED FOR UNDERWRITING", body.GetProperty("returnMessage").GetString());
        Assert.Equal("PENDING", body.GetProperty("quoteStatus").GetString());
    }

    [Fact]
    public async Task LegacyQuote_PersistsAQuoteTaggedWithTheLegacyChannel()
    {
        var response = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());
        var quoteId = Guid.Parse(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteGuid").GetString()!);

        await _factory.WithDbAsync(async db =>
        {
            var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId);

            Assert.Equal(QuoteChannel.LegacyWebForms, quote.Channel);
            Assert.Equal("Alex Johnson", quote.CustomerName);
            Assert.Equal(2023, quote.VehicleYear);
            Assert.Equal(12_000, quote.AnnualMileage);
        });
    }

    [Fact]
    public async Task LegacyQuote_AlsoWritesAnOutboxEvent()
    {
        // Same handler, so the same transactional outbox. A legacy quote is
        // underwritten by the same worker as a modern one.
        var response = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());
        var quoteId = Guid.Parse(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteGuid").GetString()!);

        await _factory.WithDbAsync(async db =>
        {
            var message = await db.OutboxMessages
                .SingleAsync(m => m.Payload.Contains(quoteId.ToString()));

            Assert.Equal("QuoteSubmitted", message.EventType);
            Assert.Null(message.ProcessedUtc);
        });
    }

    [Fact]
    public async Task LegacyAndModernSubmissions_ProduceIdenticalPricing()
    {
        // THE assertion for this seam. The same risk, submitted through both
        // contracts, must price identically — which can only hold while there is
        // exactly one rating implementation.
        var legacyResponse = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(riskState: "TX", mileage: "18000", priorCovMonths: "24"));
        var legacyQuoteId = Guid.Parse(
            (await legacyResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteGuid").GetString()!);

        var modernResponse = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName = "Alex Johnson",
            state = "TX",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 18_000,
            priorInsuranceMonths = 24
        });
        var modernQuoteId = (await modernResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(legacyQuoteId);
        await _factory.UnderwriteAsync(modernQuoteId);

        var legacyQuote = await (await _client.GetAsync($"/api/quotes/{legacyQuoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var modernQuote = await (await _client.GetAsync($"/api/quotes/{modernQuoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            modernQuote.GetProperty("status").GetString(),
            legacyQuote.GetProperty("status").GetString());
        Assert.Equal(
            modernQuote.GetProperty("annualPremium").GetDecimal(),
            legacyQuote.GetProperty("annualPremium").GetDecimal());
        Assert.Equal(
            modernQuote.GetProperty("bestCarrierId").GetString(),
            legacyQuote.GetProperty("bestCarrierId").GetString());
        Assert.Equal(
            modernQuote.GetProperty("offerCount").GetInt32(),
            legacyQuote.GetProperty("offerCount").GetInt32());

        // Only the channel differs, and that is recorded deliberately.
        Assert.Equal("LegacyWebForms", legacyQuote.GetProperty("channel").GetString());
        Assert.Equal("Modern", modernQuote.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task LegacyOfferSets_MatchTheModernOfferSetsExactly()
    {
        var legacyResponse = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());
        var legacyQuoteId = Guid.Parse(
            (await legacyResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteGuid").GetString()!);

        var modernResponse = await _client.PostAsJsonAsync("/api/quotes", new
        {
            customerName = "Alex Johnson",
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 12_000,
            priorInsuranceMonths = 36
        });
        var modernQuoteId = (await modernResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("quoteId").GetGuid();

        await _factory.UnderwriteAsync(legacyQuoteId);
        await _factory.UnderwriteAsync(modernQuoteId);

        await _factory.WithDbAsync(async db =>
        {
            var legacyOffers = await db.CarrierOffers
                .Where(o => o.QuoteId == legacyQuoteId)
                .OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => new { o.CarrierId, o.CoverageTier, o.AnnualPremium })
                .ToListAsync();

            var modernOffers = await db.CarrierOffers
                .Where(o => o.QuoteId == modernQuoteId)
                .OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => new { o.CarrierId, o.CoverageTier, o.AnnualPremium })
                .ToListAsync();

            Assert.Equal(15, legacyOffers.Count);
            Assert.Equal(modernOffers, legacyOffers);
        });
    }

    [Fact]
    public async Task LegacyEligibilityRules_MatchTheModernOnes()
    {
        // Eligibility, not just pricing, must be shared. Zero prior coverage
        // refers on both channels.
        var response = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(priorCovMonths: "0"));
        var quoteId = Guid.Parse(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("quoteGuid").GetString()!);

        await _factory.UnderwriteAsync(quoteId);

        var status = await (await _client.GetAsync($"/api/quotes/{quoteId}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Referral", status.GetProperty("status").GetString());
    }

    // =====================================================================
    // Legacy format translation
    // =====================================================================

    [Fact]
    public async Task MileageWithAThousandsSeparator_IsAccepted()
    {
        // Legacy pages post numbers back in display format. The adapter absorbs
        // that so the old page needs no change.
        var response = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(mileage: "18,500"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("returnCode").GetInt32());

        var quoteId = Guid.Parse(body.GetProperty("quoteGuid").GetString()!);
        await _factory.WithDbAsync(async db =>
            Assert.Equal(18_500, (await db.Quotes.SingleAsync(q => q.Id == quoteId)).AnnualMileage));
    }

    [Theory]
    [InlineData("", "INSURED NAME IS REQUIRED")]
    public async Task MissingInsuredName_ReturnsCodeOne(string name, string expectedMessage)
    {
        var response = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest(insuredName: name));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Still HTTP 200: the return code carries the failure, per the old contract.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("returnCode").GetInt32());
        Assert.Equal(expectedMessage, body.GetProperty("returnMessage").GetString());
    }

    [Fact]
    public async Task NonNumericVehYear_ReturnsCodeOne()
    {
        var response = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(vehYear: "twenty-twenty-three"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("returnCode").GetInt32());
        Assert.Equal("VEH YEAR IS NOT NUMERIC", body.GetProperty("returnMessage").GetString());
    }

    [Fact]
    public async Task InvalidRiskState_ReturnsCodeOne()
    {
        var response = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(riskState: "GEORGIA"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("returnCode").GetInt32());
        Assert.Contains("RISK STATE", body.GetProperty("returnMessage").GetString()!);
    }

    [Fact]
    public async Task LegacyValidationFailure_CreatesNothing()
    {
        var before = await _factory.WithDbAsync(db => db.Quotes.CountAsync());

        await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest(insuredName: ""));

        var after = await _factory.WithDbAsync(db => db.Quotes.CountAsync());
        Assert.Equal(before, after);
    }

    // =====================================================================
    // Legacy status polling
    // =====================================================================

    [Fact]
    public async Task LegacyStatusLookup_UsesTheQuoteNumberNotTheGuid()
    {
        // The legacy database only ever stored the printed quote number, so this
        // access path is what lets the old page keep polling unchanged.
        var created = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var quoteNbr = body.GetProperty("quoteNbr").GetString();
        var quoteId = Guid.Parse(body.GetProperty("quoteGuid").GetString()!);

        await _factory.UnderwriteAsync(quoteId);

        var response = await _client.GetAsync($"/legacy/quotes/{quoteNbr}");
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, status.GetProperty("returnCode").GetInt32());
        Assert.Equal("RATED", status.GetProperty("quoteStatus").GetString());
        Assert.Equal(15, status.GetProperty("offerCnt").GetInt32());
    }

    [Fact]
    public async Task LegacyStatus_FormatsThePremiumAsAFixedDecimalString()
    {
        // The old VB.NET code does CDec() on whatever it receives, so the premium
        // has to arrive as a string, not a JSON number.
        var created = await _client.PostAsJsonAsync("/legacy/quotes", LegacyRequest());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var quoteNbr = body.GetProperty("quoteNbr").GetString();
        var quoteId = Guid.Parse(body.GetProperty("quoteGuid").GetString()!);

        await _factory.UnderwriteAsync(quoteId);

        var status = await (await _client.GetAsync($"/legacy/quotes/{quoteNbr}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var premium = status.GetProperty("annualPrem");
        Assert.Equal(JsonValueKind.String, premium.ValueKind);
        Assert.Matches(@"^\d+\.\d{2}$", premium.GetString()!);
    }

    [Fact]
    public async Task LegacyStatus_ReturnsCodeOneForAnUnknownQuoteNumber()
    {
        var response = await _client.GetAsync("/legacy/quotes/Q-2026-999999");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 200 with ReturnCode 1, not a 404: the legacy contract has no notion of
        // an HTTP error body.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("returnCode").GetInt32());
        Assert.Equal("QUOTE NOT FOUND", body.GetProperty("returnMessage").GetString());
    }

    [Fact]
    public async Task LegacyStatus_ReportsAReferralDecisionMessage()
    {
        var created = await _client.PostAsJsonAsync(
            "/legacy/quotes", LegacyRequest(priorCovMonths: "0"));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var quoteNbr = body.GetProperty("quoteNbr").GetString();
        var quoteId = Guid.Parse(body.GetProperty("quoteGuid").GetString()!);

        await _factory.UnderwriteAsync(quoteId);

        var status = await (await _client.GetAsync($"/legacy/quotes/{quoteNbr}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("REFERRAL", status.GetProperty("quoteStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("decisionMsg").GetString()));
    }

    [Fact]
    public async Task LegacyResponse_CarriesTheCorrelationIdForTracing()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/legacy/quotes")
        {
            Content = JsonContent.Create(LegacyRequest())
        };
        request.Headers.Add("X-Correlation-Id", "legacy-trace-id");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("legacy-trace-id", body.GetProperty("correlationId").GetString());
    }
}
