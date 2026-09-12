using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using PolicyFlow.Application.Abstractions;
using PolicyFlow.Application.Carriers;
using PolicyFlow.Application.Payments;
using PolicyFlow.Application.Policies;
using PolicyFlow.Application.Quotes;
using PolicyFlow.Domain.Common;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Application;

/// <summary>
/// Registers application use cases, domain services and the simulated external
/// dependencies. Shared by the API and the worker so both resolve identical
/// business logic — the property that makes "one code path, two channels"
/// verifiable rather than aspirational.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // --- Domain services. Stateless and thread-safe, so singleton is correct.
        services.AddSingleton<QuoteRatingService>();
        services.AddSingleton<CarrierRatingService>();
        services.AddSingleton<IClock, SystemClock>();

        // --- Use cases. Scoped, because they depend on a scoped DbContext.
        services.AddScoped<SubmitQuoteHandler>();
        services.AddScoped<GetQuoteHandler>();
        services.AddScoped<GetQuoteOffersHandler>();
        services.AddScoped<UnderwriteQuoteHandler>();
        services.AddScoped<CreatePolicyFromQuoteHandler>();
        services.AddScoped<GetPolicyHandler>();
        services.AddScoped<SubmitPaymentHandler>();
        services.AddScoped<LegacyQuoteAdapter>();

        // --- Simulated external dependencies.
        //     Each is documented as POC-only in its own implementation file.
        services.AddSingleton<IPriorInsuranceVerifier, SimulatedPriorInsuranceVerifier>();
        services.AddSingleton<ICarrierRatingGateway, SimulatedCarrierRatingGateway>();
        services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();

        // --- Validators.
        services.AddScoped<IValidator<SubmitQuoteCommand>, SubmitQuoteCommandValidator>();

        return services;
    }
}
