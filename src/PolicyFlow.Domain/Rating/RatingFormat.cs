using System.Globalization;

namespace PolicyFlow.Domain.Rating;

/// <summary>
/// The fixed culture used to format rating traces.
/// </summary>
/// <remarks>
/// <para>
/// Rating traces are customer-facing US-dollar figures, and they are asserted in
/// tests. Formatting them with the <b>ambient</b> culture — which is what a bare
/// <c>$"{premium:C}"</c> does — makes the output depend on the machine the code
/// happens to run on.
/// </para>
/// <para>
/// That is not theoretical. The container runs with no <c>LANG</c> set, so its
/// default culture is the invariant culture, where <c>:C</c> renders the generic
/// currency sign and <c>:P0</c> inserts a space:
/// </para>
/// <code>
/// ambient (container, no LANG):  "Base annual premium: ¤795.00."   "Discounts 24 %"
/// ambient (a US dev machine):    "Base annual premium: $795.00."   "Discounts 24%"
/// </code>
/// <para>
/// The same quote rendering differently in two environments directly contradicts
/// the determinism the rating tests assert, and the wrong one reaches the UI —
/// the trace is displayed in an expandable panel on every offer card.
/// </para>
/// <para>
/// Pinning the culture fixes it at the point of formatting, which is the right
/// place: a domain service should not be relying on process-wide state for its
/// output. Setting <c>LANG</c> in the Dockerfile would mask this specific symptom
/// while leaving the dependency in the code.
/// </para>
/// <para>
/// <c>en-US</c> rather than <see cref="CultureInfo.InvariantCulture"/> because
/// these are dollar amounts for a US auto-insurance program, and the invariant
/// culture has no currency symbol.
/// </para>
/// </remarks>
public static class RatingFormat
{
    /// <summary>
    /// Culture for every monetary, percentage and thousands-separated value in a
    /// rating trace or a decline reason.
    /// </summary>
    /// <remarks>
    /// Falls back to a hand-built <c>en-US</c> if ICU data is unavailable (a
    /// chiseled or Alpine image without the ICU package, or
    /// <c>InvariantGlobalization=true</c>). The fallback keeps the dollar sign and
    /// the grouping separators rather than silently reverting to <c>¤</c>.
    /// </remarks>
    public static CultureInfo Culture { get; } = CreateCulture();

    private static CultureInfo CreateCulture()
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo("en-US");

            // In globalization-invariant mode GetCultureInfo succeeds but returns
            // a culture whose currency symbol is the generic sign, so the name
            // check alone is not enough.
            if (culture.NumberFormat.CurrencySymbol == "$")
            {
                return culture;
            }
        }
        catch (CultureNotFoundException)
        {
            // No ICU data. Fall through to the explicit format below.
        }

        var fallback = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        fallback.NumberFormat.CurrencySymbol = "$";
        fallback.NumberFormat.CurrencyDecimalSeparator = ".";
        fallback.NumberFormat.CurrencyGroupSeparator = ",";
        // 0 = "$n" with the symbol immediately before the value, no space.
        fallback.NumberFormat.CurrencyPositivePattern = 0;
        fallback.NumberFormat.NumberDecimalSeparator = ".";
        fallback.NumberFormat.NumberGroupSeparator = ",";
        fallback.NumberFormat.PercentDecimalSeparator = ".";
        fallback.NumberFormat.PercentGroupSeparator = ",";
        fallback.NumberFormat.PercentSymbol = "%";
        // 1 = "n%" with no space between the value and the symbol.
        fallback.NumberFormat.PercentPositivePattern = 1;

        return CultureInfo.ReadOnly(fallback);
    }
}
