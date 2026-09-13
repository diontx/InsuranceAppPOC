using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Carriers;
using PolicyFlow.Application.Payments;
using PolicyFlow.Application.Policies;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Application.Underwriting;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;
using PolicyFlow.Infrastructure.Persistence;
using PolicyFlow.Infrastructure.Repositories;

namespace PolicyFlow.Application.Tests;

/// <summary>
/// Wires the real handlers, repositories and unit of work over an in-memory
/// EF Core provider.
/// </summary>
/// <remarks>
/// <para>
/// Real handlers and real repositories, so these tests exercise the actual
/// application logic and EF Core query shapes rather than a mock's idea of them.
/// Only the external dependencies (verification bureau, carrier gateways, payment
/// gateway) are substituted, and even those are the same simulated
/// implementations production uses, with the delays intact.
/// </para>
/// <para>
/// <b>What the in-memory provider cannot test.</b> It has no transactions, no
/// unique indexes and no <c>rowversion</c>. The SQL-enforced guarantees —
/// idempotency, duplicate-bind prevention, optimistic concurrency — are therefore
/// verified by <c>PolicyFlow.Api.IntegrationTests</c> against a real SQL Server
/// container. These tests cover the application-level behaviour that sits above
/// those constraints.
/// </para>
/// </remarks>
internal sealed class TestHarness : IAsyncDisposable
{
    public PolicyFlowDbContext Db { get; }
    public FixedClock Clock { get; }
    public TestCorrelationContext Correlation { get; }
    public IQuoteRepository Quotes { get; }
    public IPolicyRepository Policies { get; }
    public IPaymentRepository Payments { get; }
    public IOutboxRepository Outbox { get; }
    public IProcessedMessageRepository ProcessedMessages { get; }
    public IUnitOfWork UnitOfWork { get; }
    public RecordingComparisonStore ComparisonStore { get; }
    public StubCarrierCatalog Catalog { get; }

    public SubmitQuoteHandler SubmitQuote { get; }
    public GetQuoteHandler GetQuote { get; }
    public GetQuoteOffersHandler GetOffers { get; }
    public CarrierMarketplace Marketplace { get; }
    public UnderwriteQuoteHandler Underwrite { get; }
    public GetUnderwritingQueueHandler UnderwritingQueue { get; }
    public GetUnderwritingReviewHandler UnderwritingReview { get; }
    public ReviewReferralHandler ReviewReferral { get; }
    public CreatePolicyFromQuoteHandler CreatePolicy { get; }
    public GetPolicyHandler GetPolicy { get; }
    public SubmitPaymentHandler SubmitPayment { get; }
    public LegacyQuoteAdapter LegacyAdapter { get; }

    public TestHarness(IReadOnlyList<CarrierProfile>? carriers = null)
    {
        var options = new DbContextOptionsBuilder<PolicyFlowDbContext>()
            // A unique database name per harness, so tests cannot see each
            // other's rows however they are scheduled.
            .UseInMemoryDatabase($"policyflow-tests-{Guid.NewGuid()}")
            // The in-memory provider warns that transactions are ignored. That is
            // expected and documented above, so the warning is suppressed rather
            // than failing every test that opens one.
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Db = new PolicyFlowDbContext(options);
        Clock = new FixedClock(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        Correlation = new TestCorrelationContext();

        Quotes = new QuoteRepository(Db);
        Policies = new PolicyRepository(Db);
        Payments = new PaymentRepository(Db);
        Outbox = new OutboxRepository(Db);
        ProcessedMessages = new ProcessedMessageRepository(Db);
        UnitOfWork = new UnitOfWork(Db, NullLogger<UnitOfWork>.Instance);

        ComparisonStore = new RecordingComparisonStore();
        Catalog = new StubCarrierCatalog(carriers ?? CarrierPanel.All);

        var quoteRating = new QuoteRatingService();
        var carrierRating = new CarrierRatingService();
        var verifier = new SimulatedPriorInsuranceVerifier(
            NullLogger<SimulatedPriorInsuranceVerifier>.Instance);
        var carrierGateway = new SimulatedCarrierRatingGateway(
            carrierRating, Clock, NullLogger<SimulatedCarrierRatingGateway>.Instance);
        var paymentGateway = new SimulatedPaymentGateway(
            NullLogger<SimulatedPaymentGateway>.Instance);

        SubmitQuote = new SubmitQuoteHandler(
            Quotes, Outbox, UnitOfWork, Clock, Correlation,
            NullLogger<SubmitQuoteHandler>.Instance);

        GetQuote = new GetQuoteHandler(Quotes);

        GetOffers = new GetQuoteOffersHandler(
            ComparisonStore, Quotes, Catalog, NullLogger<GetQuoteOffersHandler>.Instance);

        Marketplace = new CarrierMarketplace(
            Catalog, carrierGateway, ComparisonStore, Clock, NullLogger<CarrierMarketplace>.Instance);

        Underwrite = new UnderwriteQuoteHandler(
            Quotes, UnitOfWork, Marketplace, verifier,
            quoteRating, Clock, NullLogger<UnderwriteQuoteHandler>.Instance);

        UnderwritingQueue = new GetUnderwritingQueueHandler(Quotes, Clock);

        UnderwritingReview = new GetUnderwritingReviewHandler(Quotes, Catalog, quoteRating, carrierRating);

        ReviewReferral = new ReviewReferralHandler(
            Quotes, UnitOfWork, Marketplace, UnderwritingReview, Clock,
            NullLogger<ReviewReferralHandler>.Instance);

        CreatePolicy = new CreatePolicyFromQuoteHandler(
            Quotes, Policies, UnitOfWork, Clock, Correlation,
            NullLogger<CreatePolicyFromQuoteHandler>.Instance);

        GetPolicy = new GetPolicyHandler(Policies, Quotes);

        SubmitPayment = new SubmitPaymentHandler(
            Policies, Payments, UnitOfWork, paymentGateway, Clock, Correlation,
            NullLogger<SubmitPaymentHandler>.Instance);

        LegacyAdapter = new LegacyQuoteAdapter(
            SubmitQuote, NullLogger<LegacyQuoteAdapter>.Instance);
    }

    /// <summary>Submits a quote and runs underwriting, returning the rated quote.</summary>
    public async Task<Quote> SubmitAndUnderwriteAsync(
        string customerName = "Alex Johnson",
        string state = "GA",
        int? vehicleYear = null,
        int annualMileage = 10_000,
        int priorInsuranceMonths = 36,
        QuoteChannel channel = QuoteChannel.Modern)
    {
        var submitted = await SubmitQuote.HandleAsync(new SubmitQuoteCommand
        {
            CustomerName = customerName,
            State = state,
            VehicleYear = vehicleYear ?? Clock.UtcNow.Year - 3,
            VehicleMake = "Toyota",
            VehicleModel = "Camry",
            AnnualMileage = annualMileage,
            PriorInsuranceMonths = priorInsuranceMonths,
            Channel = channel
        });

        await Underwrite.HandleAsync(submitted.QuoteId);

        // Cleared so the next read returns persisted state rather than the
        // instances the handler already had tracked.
        Db.ChangeTracker.Clear();

        return (await Quotes.GetWithOffersAsync(submitted.QuoteId))!;
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}

/// <summary>A clock that does not move, so time-dependent rules are deterministic.</summary>
internal sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
}

internal sealed class TestCorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = "test-correlation-id";
}

/// <summary>Serves a fixed carrier panel without touching MongoDB.</summary>
internal sealed class StubCarrierCatalog(IReadOnlyList<CarrierProfile> carriers) : ICarrierCatalog
{
    public int SeedCallCount { get; private set; }

    public Task<IReadOnlyList<CarrierProfile>> GetActiveCarriersAsync(CancellationToken ct = default) =>
        Task.FromResult(carriers);

    public Task<CarrierProfile?> GetCarrierAsync(string carrierId, CancellationToken ct = default) =>
        Task.FromResult(carriers.FirstOrDefault(
            c => c.CarrierId.Equals(carrierId, StringComparison.OrdinalIgnoreCase)));

    public Task<int> SeedAsync(CancellationToken ct = default)
    {
        SeedCallCount++;
        return Task.FromResult(carriers.Count);
    }
}

/// <summary>
/// In-memory comparison store that records what was written, so tests can assert
/// on the read model without a Mongo container.
/// </summary>
internal sealed class RecordingComparisonStore : IQuoteComparisonStore
{
    private readonly Dictionary<Guid, QuoteComparisonDocument> _documents = [];

    public int UpsertCount { get; private set; }

    /// <summary>When true, every write throws — used to prove failures are non-fatal.</summary>
    public bool FailWrites { get; set; }

    /// <summary>When true, reads return null — used to prove the SQL fallback works.</summary>
    public bool FailReads { get; set; }

    public Task UpsertAsync(QuoteComparisonDocument document, CancellationToken ct = default)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("Simulated document store outage.");
        }

        UpsertCount++;
        _documents[document.QuoteId] = document;
        return Task.CompletedTask;
    }

    public Task<QuoteComparisonDocument?> GetByQuoteIdAsync(Guid quoteId, CancellationToken ct = default)
    {
        if (FailReads)
        {
            return Task.FromResult<QuoteComparisonDocument?>(null);
        }

        return Task.FromResult(_documents.TryGetValue(quoteId, out var doc) ? doc : null);
    }

    public Task<QuoteComparisonDocument?> GetByQuoteNumberAsync(
        string quoteNumber,
        CancellationToken ct = default)
    {
        if (FailReads)
        {
            return Task.FromResult<QuoteComparisonDocument?>(null);
        }

        return Task.FromResult(_documents.Values.FirstOrDefault(d => d.QuoteNumber == quoteNumber));
    }
}
