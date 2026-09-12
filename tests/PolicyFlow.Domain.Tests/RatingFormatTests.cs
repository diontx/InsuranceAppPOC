using System.Globalization;
using PolicyFlow.Domain.Carriers;
using PolicyFlow.Domain.Quotes;
using PolicyFlow.Domain.Rating;

namespace PolicyFlow.Domain.Tests;

/// <summary>
/// Rating traces must render identically regardless of the ambient culture of the
/// machine running the code.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because of a real defect. The traces originally used a bare
/// <c>$"{premium:C}"</c>, which formats with <see cref="CultureInfo.CurrentCulture"/>.
/// The API container runs with no <c>LANG</c> set, so its default culture is the
/// invariant culture — and the customer-facing trace rendered as:
/// </para>
/// <code>
/// "Base annual premium: ¤795.00."     instead of    "$795.00."
/// "Discounts 24 % (...)"              instead of    "Discounts 24% (...)"
/// </code>
/// <para>
/// The trace is displayed in an expandable panel on every offer card, so the wrong
/// output reached the UI. It also meant the same quote rendered differently on a
/// developer's machine than in the container, which directly contradicts the
/// determinism the other rating tests assert.
/// </para>
/// <para>
/// These tests run the rating services under deliberately hostile cultures. A
/// German or French culture uses a comma decimal separator and a trailing currency
/// symbol, so any un-pinned format string fails loudly here.
/// </para>
/// </remarks>
public class RatingFormatTests
{
    /// <summary>
    /// Cultures chosen to break different assumptions: invariant (no currency
    /// symbol — the actual container default), German (comma decimal, trailing
    /// symbol, space before %), French (non-breaking-space group separator), and
    /// Japanese (no decimal places on currency).
    /// </summary>
    private static readonly string[] CultureNames =
    [
        "",         // InvariantCulture — what the container actually uses
        "de-DE",
        "fr-FR",
        "ja-JP",
        "en-GB",
    ];

    /// <summary>The same list, shaped for [MemberData].</summary>
    public static TheoryData<string> HostileCultures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in CultureNames)
            {
                data.Add(name);
            }
            return data;
        }
    }

    /// <summary>
    /// Runs an action with <see cref="CultureInfo.CurrentCulture"/> forced, then
    /// restores it. Restoring in a finally matters: leaking a culture would make
    /// unrelated tests fail depending on execution order.
    /// </summary>
    private static T WithCulture<T>(string cultureName, Func<T> action)
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;

        try
        {
            var culture = cultureName.Length == 0
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(cultureName);

            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Fact]
    public void RatingFormatCulture_HasADollarCurrencySymbol()
    {
        // The whole point: never the generic currency sign.
        Assert.Equal("$", RatingFormat.Culture.NumberFormat.CurrencySymbol);
        Assert.Equal(".", RatingFormat.Culture.NumberFormat.CurrencyDecimalSeparator);
        Assert.Equal(",", RatingFormat.Culture.NumberFormat.CurrencyGroupSeparator);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void BaselineTrace_UsesDollarSigns_UnderAnyAmbientCulture(string culture)
    {
        var sut = new QuoteRatingService();

        var trace = WithCulture(culture, () =>
            sut.Evaluate(TestQuotes.Clean(), TestQuotes.Now).Trace);

        var joined = string.Join("\n", trace);

        Assert.Contains("$900.00", joined);
        // ¤ is the generic currency sign the invariant culture emits. Its presence
        // means a format string somewhere lost its explicit culture.
        Assert.DoesNotContain("¤", joined);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void CarrierTrace_UsesDollarSigns_UnderAnyAmbientCulture(string culture)
    {
        var sut = new CarrierRatingService();
        var carrier = CarrierPanel.Find("ironclad-direct")!;

        var offers = WithCulture(culture, () =>
            sut.RateCarrier(
                TestQuotes.Clean(vehicleYear: TestQuotes.CurrentYear, annualMileage: 20_000),
                carrier,
                TestQuotes.Now));

        var joined = string.Join("\n", offers.SelectMany(o => o.RatingTrace.Split('\n')));

        Assert.Contains("$", joined);
        Assert.DoesNotContain("¤", joined);
        // German and French would render 795,00 rather than 795.00.
        Assert.Contains("$795.00", joined);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void PercentageInTrace_HasNoSpaceBeforeTheSign(string culture)
    {
        // de-DE renders "24 %" with a non-breaking space; en-US renders "24%".
        var sut = new CarrierRatingService();
        var carrier = CarrierPanel.Find("ironclad-direct")!;

        var offers = WithCulture(culture, () =>
            sut.RateCarrier(
                TestQuotes.Clean(priorInsuranceMonths: 48, annualMileage: 5_000),
                carrier,
                TestQuotes.Now));

        var discountLines = offers
            .SelectMany(o => o.RatingTrace.Split('\n'))
            .Where(l => l.Contains("Discounts"))
            .ToList();

        Assert.NotEmpty(discountLines);
        foreach (var line in discountLines)
        {
            Assert.DoesNotContain(" %", line);
            Assert.DoesNotContain(" %", line);   // non-breaking space
            Assert.DoesNotContain(" %", line);   // narrow no-break space (fr-FR)
        }
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void ThousandsSeparatorInTrace_IsAComma(string culture)
    {
        // fr-FR uses a narrow no-break space as the group separator, de-DE a period.
        var sut = new QuoteRatingService();

        var trace = WithCulture(culture, () =>
            sut.Evaluate(TestQuotes.Clean(annualMileage: 20_000), TestQuotes.Now).Trace);

        var mileageLine = Assert.Single(trace, l => l.Contains("Annual mileage"));

        Assert.Contains("20,000", mileageLine);
        Assert.Contains("15,000", mileageLine);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void MileageDeclineReason_UsesACommaSeparator(string culture)
    {
        var sut = new CarrierRatingService();
        var carrier = CarrierPanel.Find("blueridge-assurance")!;   // caps at 20,000

        var reason = WithCulture(culture, () =>
        {
            sut.IsInAppetite(TestQuotes.Clean(annualMileage: 25_000), carrier, out var r);
            return r;
        });

        Assert.NotNull(reason);
        Assert.Contains("20,000", reason);
        Assert.Contains("25,000", reason);
    }

    [Fact]
    public void TraceIsByteIdenticalAcrossEveryCulture()
    {
        // The strongest form of the assertion: not just "looks right" in each
        // culture, but identical in all of them.
        var sut = new CarrierRatingService();
        var carrier = CarrierPanel.Find("liberty-crest")!;
        var quote = TestQuotes.Clean(
            state: "FL", vehicleYear: TestQuotes.CurrentYear, annualMileage: 18_000);

        var renderings = CultureNames
            .Select(culture => WithCulture(culture, () =>
                string.Join("\n", sut.RateCarrier(quote, carrier, TestQuotes.Now)
                    .Select(o => o.RatingTrace))))
            .Distinct()
            .ToList();

        Assert.Single(renderings);
    }

    [Fact]
    public void BaselineTraceIsByteIdenticalAcrossEveryCulture()
    {
        var sut = new QuoteRatingService();
        var quote = TestQuotes.Clean(
            state: "TX", vehicleYear: TestQuotes.CurrentYear, annualMileage: 22_000,
            priorInsuranceMonths: 2);

        var renderings = CultureNames
            .Select(culture => WithCulture(culture, () =>
                string.Join("\n", sut.Evaluate(quote, TestQuotes.Now).Trace)))
            .Distinct()
            .ToList();

        Assert.Single(renderings);
    }
}
