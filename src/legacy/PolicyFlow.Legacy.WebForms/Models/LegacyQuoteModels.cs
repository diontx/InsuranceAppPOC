using System;
using System.Runtime.Serialization;

namespace PolicyFlow.Legacy.WebForms.Models
{
    /// <summary>
    /// The legacy quote request, in the shape this application has always posted:
    /// PascalCase names, every value a string, abbreviated field names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These names are not a style choice — they are the existing wire contract.
    /// The modern API accepts them as-is through <c>POST /legacy/quotes</c>
    /// precisely so this application does not have to change. Renaming
    /// <c>VehYear</c> to <c>VehicleYear</c> here would mean a release of the
    /// legacy application, a regression pass over every page that touches it, and
    /// a deployment window — for no behavioural benefit.
    /// </para>
    /// <para>
    /// Everything is <see cref="string"/> because Web Forms controls yield
    /// strings. <c>TextBox.Text</c> has no type, so the legacy application never
    /// had typed values to send.
    /// </para>
    /// </remarks>
    [DataContract]
    public class LegacyQuoteRequest
    {
        [DataMember(Name = "InsuredName")]
        public string InsuredName { get; set; }

        /// <summary>Two-letter state code of the garaging address.</summary>
        [DataMember(Name = "RiskState")]
        public string RiskState { get; set; }

        [DataMember(Name = "VehYear")]
        public string VehYear { get; set; }

        [DataMember(Name = "VehMake")]
        public string VehMake { get; set; }

        [DataMember(Name = "VehModel")]
        public string VehModel { get; set; }

        /// <summary>
        /// Annual mileage. May arrive with a thousands separator, e.g. "12,000",
        /// because the page formats it for display and posts back what it shows.
        /// The modern adapter absorbs that rather than requiring a change here.
        /// </summary>
        [DataMember(Name = "Mileage")]
        public string Mileage { get; set; }

        [DataMember(Name = "PriorCovMonths")]
        public string PriorCovMonths { get; set; }
    }

    /// <summary>
    /// The legacy response: a numeric return code and a message, the way the
    /// original ASMX service answered.
    /// </summary>
    /// <remarks>
    /// The legacy client predates <c>ProblemDetails</c> and cannot interpret a
    /// 4xx body, so the compatibility endpoint always returns HTTP 200 and puts
    /// the outcome in <see cref="ReturnCode"/>. Keeping that convention is what
    /// lets these pages keep working untouched.
    /// </remarks>
    [DataContract]
    public class LegacyQuoteResponse
    {
        /// <summary>0 = accepted, 1 = validation failure, 2 = system error.</summary>
        [DataMember(Name = "returnCode")]
        public int ReturnCode { get; set; }

        [DataMember(Name = "returnMessage")]
        public string ReturnMessage { get; set; }

        /// <summary>Customer-facing quote number, e.g. Q-2026-000042.</summary>
        [DataMember(Name = "quoteNbr")]
        public string QuoteNbr { get; set; }

        /// <summary>
        /// The modern quote GUID. Captured in ViewState so the status page can
        /// poll, but deliberately not persisted: the legacy database schema has no
        /// column for it, and adding one would be a migration this modernization
        /// step does not need.
        /// </summary>
        [DataMember(Name = "quoteGuid")]
        public string QuoteGuid { get; set; }

        [DataMember(Name = "quoteStatus")]
        public string QuoteStatus { get; set; }

        [DataMember(Name = "statusUrl")]
        public string StatusUrl { get; set; }

        [DataMember(Name = "correlationId")]
        public string CorrelationId { get; set; }

        public bool IsAccepted
        {
            get { return ReturnCode == 0; }
        }
    }

    /// <summary>Status response in the legacy field naming and formatting.</summary>
    [DataContract]
    public class LegacyQuoteStatusResponse
    {
        /// <summary>0 = found, 1 = not found, 2 = system error.</summary>
        [DataMember(Name = "returnCode")]
        public int ReturnCode { get; set; }

        [DataMember(Name = "returnMessage")]
        public string ReturnMessage { get; set; }

        [DataMember(Name = "quoteNbr")]
        public string QuoteNbr { get; set; }

        /// <summary>Uppercase status, as the legacy UI expects.</summary>
        [DataMember(Name = "quoteStatus")]
        public string QuoteStatus { get; set; }

        /// <summary>
        /// Premium as a fixed-decimal string, e.g. "1482.50". The original VB.NET
        /// code does <c>CDec()</c> on whatever it receives, so a JSON number would
        /// have broken it on any machine with a non-US locale.
        /// </summary>
        [DataMember(Name = "annualPrem")]
        public string AnnualPrem { get; set; }

        [DataMember(Name = "bestCarrier")]
        public string BestCarrier { get; set; }

        [DataMember(Name = "offerCnt")]
        public int OfferCnt { get; set; }

        [DataMember(Name = "decisionMsg")]
        public string DecisionMsg { get; set; }

        [DataMember(Name = "correlationId")]
        public string CorrelationId { get; set; }

        public bool IsFound
        {
            get { return ReturnCode == 0; }
        }

        /// <summary>
        /// True once underwriting has reached a final decision, so the page can
        /// stop its polling timer.
        /// </summary>
        public bool IsTerminal
        {
            get
            {
                if (string.IsNullOrEmpty(QuoteStatus))
                {
                    return false;
                }

                return QuoteStatus == "RATED"
                    || QuoteStatus == "REFERRAL"
                    || QuoteStatus == "DECLINED";
            }
        }

        /// <summary>
        /// Parses <see cref="AnnualPrem"/> with the invariant culture.
        /// </summary>
        /// <remarks>
        /// Invariant, not current culture. The API always sends a period as the
        /// decimal separator; parsing with a culture that uses a comma would turn
        /// "1482.50" into 148250 — the classic locale bug in legacy integrations.
        /// </remarks>
        public decimal? ParsedPremium
        {
            get
            {
                decimal premium;
                if (decimal.TryParse(
                        AnnualPrem,
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out premium))
                {
                    return premium;
                }

                return null;
            }
        }
    }
}
