using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Quotes;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// The legacy modernization seam.
/// </summary>
/// <remarks>
/// The property these tests defend is the one that makes incremental
/// modernization safe: <b>the legacy contract and the modern API produce
/// identical business outcomes, because they share one implementation.</b> If a
/// rating rule were ever copied into the adapter, the two channels would drift
/// and start quoting different prices for the same risk — the exact failure mode
/// that makes big-bang rewrites tempting and partial migrations dangerous.
/// </remarks>
public class LegacyQuoteAdapterTests
{
    private static LegacyQuoteRequest ValidLegacyRequest(
        string insuredName = "Alex Johnson",
        string riskState = "GA",
        string vehYear = "2023",
        string mileage = "12000",
        string priorCovMonths = "36") => new()
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
    public async Task ValidLegacyRequest_IsAcceptedWithReturnCodeZero()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());

        Assert.Equal(0, response.ReturnCode);
        Assert.Equal("QUOTE ACCEPTED FOR UNDERWRITING", response.ReturnMessage);
        Assert.NotNull(response.QuoteNbr);
        Assert.Equal("PENDING", response.QuoteStatus);
    }

    [Fact]
    public async Task LegacyRequest_CreatesTheSamePendingQuoteAsTheModernApi()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());
        var quote = await harness.Quotes.GetByQuoteNumberAsync(response.QuoteNbr!);

        Assert.NotNull(quote);
        Assert.Equal(QuoteStatus.Pending, quote!.Status);
        Assert.Equal("Alex Johnson", quote.CustomerName);
        Assert.Equal("GA", quote.State);
        Assert.Equal(2023, quote.VehicleYear);
        Assert.Equal(12_000, quote.AnnualMileage);
        Assert.Equal(36, quote.PriorInsuranceMonths);
    }

    [Fact]
    public async Task LegacyRequest_IsTaggedWithTheLegacyChannel()
    {
        // The only intentional difference between the channels. It exists so
        // traffic still arriving from the old system is visible in data and logs,
        // which is how you know when it is safe to retire.
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());
        var quote = await harness.Quotes.GetByQuoteNumberAsync(response.QuoteNbr!);

        Assert.Equal(QuoteChannel.LegacyWebForms, quote!.Channel);
    }

    [Fact]
    public async Task LegacyRequest_AlsoWritesTheOutboxEvent()
    {
        // Same handler, so the same transactional-outbox behaviour. A legacy quote
        // is underwritten by the same worker as a modern one.
        await using var harness = new TestHarness();

        await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());

        var message = Assert.Single(harness.Db.OutboxMessages.ToList());
        Assert.Equal(Domain.Messaging.QuoteSubmittedEvent.EventType, message.EventType);
        Assert.Null(message.ProcessedUtc);
    }

    [Fact]
    public async Task LegacyAndModernChannels_RateIdentically()
    {
        // THE test. The same risk submitted both ways must price identically,
        // which can only hold while there is exactly one rating implementation.
        await using var legacyHarness = new TestHarness();
        await using var modernHarness = new TestHarness();

        var legacyResponse = await legacyHarness.LegacyAdapter.SubmitAsync(
            ValidLegacyRequest(riskState: "TX", mileage: "18000", priorCovMonths: "24"));
        await legacyHarness.Underwrite.HandleAsync(Guid.Parse(legacyResponse.QuoteGuid!));
        legacyHarness.Db.ChangeTracker.Clear();
        var legacyQuote = await legacyHarness.Quotes.GetWithOffersAsync(
            Guid.Parse(legacyResponse.QuoteGuid!));

        var modernQuote = await modernHarness.SubmitAndUnderwriteAsync(
            state: "TX", vehicleYear: 2023, annualMileage: 18_000, priorInsuranceMonths: 24);

        Assert.Equal(modernQuote.Status, legacyQuote!.Status);
        Assert.Equal(modernQuote.AnnualPremium, legacyQuote.AnnualPremium);
        Assert.Equal(modernQuote.BestCarrierId, legacyQuote.BestCarrierId);
        Assert.Equal(modernQuote.Offers.Count, legacyQuote.Offers.Count);

        Assert.Equal(
            modernQuote.Offers.OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium)),
            legacyQuote.Offers.OrderBy(o => o.CarrierId).ThenBy(o => o.CoverageTier)
                .Select(o => (o.CarrierId, o.CoverageTier, o.AnnualPremium)));
    }

    [Fact]
    public async Task LegacyReferralOutcome_MatchesTheModernOutcome()
    {
        // Eligibility rules must apply to both channels too, not just pricing.
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(
            ValidLegacyRequest(priorCovMonths: "0"));
        await harness.Underwrite.HandleAsync(Guid.Parse(response.QuoteGuid!));
        harness.Db.ChangeTracker.Clear();

        var quote = await harness.Quotes.GetByIdAsync(Guid.Parse(response.QuoteGuid!));
        Assert.Equal(QuoteStatus.Referral, quote!.Status);
    }

    // =====================================================================
    // Legacy format translation
    // =====================================================================

    [Fact]
    public async Task MileageWithThousandsSeparator_IsParsed()
    {
        // Legacy Web Forms pages format numbers for display and post them back
        // formatted. The adapter absorbs that rather than making the old page
        // change, which would need its own release.
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(
            ValidLegacyRequest(mileage: "18,500"));

        Assert.Equal(0, response.ReturnCode);
        var quote = await harness.Quotes.GetByQuoteNumberAsync(response.QuoteNbr!);
        Assert.Equal(18_500, quote!.AnnualMileage);
    }

    [Fact]
    public async Task LowercaseRiskState_IsNormalised()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(riskState: "ga"));
        var quote = await harness.Quotes.GetByQuoteNumberAsync(response.QuoteNbr!);

        Assert.Equal("GA", quote!.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingInsuredName_ReturnsCodeOne(string? name)
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(insuredName: name!));

        Assert.Equal(1, response.ReturnCode);
        Assert.Equal("INSURED NAME IS REQUIRED", response.ReturnMessage);
        Assert.Null(response.QuoteNbr);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("GEORGIA")]
    public async Task InvalidRiskState_ReturnsCodeOne(string state)
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(riskState: state));

        Assert.Equal(1, response.ReturnCode);
        Assert.Contains("RISK STATE", response.ReturnMessage);
    }

    [Theory]
    [InlineData("not-a-year")]
    [InlineData("")]
    public async Task NonNumericVehYear_ReturnsCodeOne(string year)
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(vehYear: year));

        Assert.Equal(1, response.ReturnCode);
        Assert.Equal("VEH YEAR IS NOT NUMERIC", response.ReturnMessage);
    }

    [Fact]
    public async Task NonNumericMileage_ReturnsCodeOne()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(mileage: "lots"));

        Assert.Equal(1, response.ReturnCode);
        Assert.Equal("MILEAGE IS NOT NUMERIC", response.ReturnMessage);
    }

    [Fact]
    public async Task NonNumericPriorCovMonths_ReturnsCodeOne()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(
            ValidLegacyRequest(priorCovMonths: "n/a"));

        Assert.Equal(1, response.ReturnCode);
        Assert.Equal("PRIOR COV MONTHS IS NOT NUMERIC", response.ReturnMessage);
    }

    [Fact]
    public async Task ValidationFailure_CreatesNoQuoteAndNoOutboxRow()
    {
        // A rejected legacy request must leave nothing behind, or the worker would
        // later pick up an event for a quote that was never really accepted.
        await using var harness = new TestHarness();

        await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest(insuredName: ""));

        Assert.Empty(harness.Db.Quotes.ToList());
        Assert.Empty(harness.Db.OutboxMessages.ToList());
    }

    [Fact]
    public async Task DomainRuleViolation_IsTranslatedIntoTheLegacyReturnCode()
    {
        // The legacy client predates ProblemDetails and cannot interpret a 4xx
        // body, so a modern domain error has to come back as ReturnCode 1.
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(
            ValidLegacyRequest(vehYear: "1850"));

        Assert.Equal(1, response.ReturnCode);
        Assert.False(string.IsNullOrWhiteSpace(response.ReturnMessage));
    }

    [Fact]
    public async Task LegacyResponse_CarriesTheCorrelationIdForTracing()
    {
        // Lets an operator trace a legacy-originated quote through the modern
        // stack from the return value the old page already logs.
        await using var harness = new TestHarness();
        harness.Correlation.CorrelationId = "legacy-trace-99";

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());

        Assert.Equal("legacy-trace-99", response.CorrelationId);
    }

    [Fact]
    public async Task LegacyResponse_ReturnsAStatusUrlForPolling()
    {
        await using var harness = new TestHarness();

        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());

        Assert.Equal($"/api/quotes/{response.QuoteGuid}", response.StatusUrl);
    }

    [Fact]
    public async Task LegacyStatusLookup_ByQuoteNumberWorks()
    {
        // The legacy page polls by printed quote number because its own database
        // never stored the GUID.
        await using var harness = new TestHarness();
        var response = await harness.LegacyAdapter.SubmitAsync(ValidLegacyRequest());

        var view = await harness.GetQuote.LookupByQuoteNumberAsync(response.QuoteNbr!);

        Assert.NotNull(view);
        Assert.Equal(response.QuoteNbr, view!.QuoteNumber);
        Assert.Equal("LegacyWebForms", view.Channel);
    }

    [Fact]
    public async Task LegacyStatusLookup_ReturnsNullForAnUnknownNumber()
    {
        await using var harness = new TestHarness();

        Assert.Null(await harness.GetQuote.LookupByQuoteNumberAsync("Q-2026-999999"));
    }

    [Fact]
    public async Task Adapter_RejectsNullRequest()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.LegacyAdapter.SubmitAsync(null!));
    }
}
