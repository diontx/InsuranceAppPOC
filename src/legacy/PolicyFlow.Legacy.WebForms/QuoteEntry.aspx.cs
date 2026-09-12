using System;
using System.Globalization;
using System.Web.UI;
using PolicyFlow.Legacy.WebForms.Models;
using PolicyFlow.Legacy.WebForms.Services;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Code-behind for the legacy quote entry page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This file is the point of the whole exercise.</b> It is worth reading
    /// alongside <c>docs/LEGACY-MODERNIZATION.md</c>.
    /// </para>
    /// <para>
    /// <b>What this code-behind used to contain.</b> Before modernization,
    /// <c>btnSubmit_Click</c> ran the entire quote workflow inline: parse the
    /// textboxes, apply the base premium, add surcharges for vehicle age and
    /// mileage and coverage lapse, look up the state factor from a hard-coded
    /// table, decide eligibility, open a <c>SqlConnection</c>, insert the quote
    /// with a concatenated INSERT statement, and write the premium into
    /// <c>lblPremium.Text</c>. Several hundred lines, all of it inseparable from
    /// the page lifecycle.
    /// </para>
    /// <para>
    /// <b>Why that was the real problem.</b> Not that it was old — that the
    /// business rules could not be reached from anywhere else. They could not be
    /// unit tested, because instantiating them meant instantiating a
    /// <see cref="Page"/> with an <c>HttpContext</c>. They could not be reused,
    /// so when the mobile channel needed quoting, the rules were <i>copied</i> —
    /// and the two copies drifted until the same customer got different prices
    /// depending on which channel they used.
    /// </para>
    /// <para>
    /// <b>What it contains now.</b> Read the controls, hand them to the API, render
    /// the response. There is no premium arithmetic, no eligibility rule and no
    /// SQL anywhere in this project. The rules live once, in the modern domain
    /// layer, and this page and the React SPA reach the identical implementation.
    /// </para>
    /// <para>
    /// This page is now thin enough to delete. That is the goal: the seam exists so
    /// the legacy UI can be retired when a replacement screen proves itself, rather
    /// than having to be rewritten in lockstep with the back end.
    /// </para>
    /// </remarks>
    public partial class QuoteEntry : Page
    {
        private readonly PolicyFlowApiClient _apiClient = new PolicyFlowApiClient();

        /// <summary>
        /// ViewState key for the correlation id.
        /// </summary>
        /// <remarks>
        /// Held in ViewState so the id survives the postback and can be shown to
        /// the user and used by the status page. ViewState is the Web Forms answer
        /// to per-page state, and it is also its defining cost: this value is
        /// serialised into a hidden field and sent to the browser and back on
        /// every single postback.
        /// </remarks>
        private const string CorrelationIdKey = "PolicyFlow.CorrelationId";

        /// <summary>
        /// Fires on every request, postback included, which is exactly the
        /// lifecycle subtlety that makes Web Forms code-behind hard to reason
        /// about. <c>IsPostBack</c> is the guard that separates "first load" from
        /// "the user clicked something".
        /// </summary>
        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                // First load only. Without the IsPostBack guard this would wipe
                // the user's input on every click — a bug every Web Forms
                // developer writes exactly once.
                txtVehYear.Text = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
                txtMileage.Text = "12,000";
                txtPriorCovMonths.Text = "36";

                // The year validator's upper bound has to be set at runtime:
                // hard-coding it in the .aspx means the page silently rejects
                // next year's models the moment the calendar turns over.
                rngVehYear.MaximumValue =
                    (DateTime.Now.Year + 1).ToString(CultureInfo.InvariantCulture);

                ViewState[CorrelationIdKey] = Guid.NewGuid().ToString();
            }
        }

        /// <summary>Submits the quote through the compatibility seam.</summary>
        protected void btnSubmit_Click(object sender, EventArgs e)
        {
            // Server-side validators have already run by the time this fires.
            // Checking IsValid is still required: a handler that skips it
            // processes invalid input, because Web Forms does not stop the event.
            if (!Page.IsValid)
            {
                return;
            }

            pnlServiceError.Visible = false;

            var request = new LegacyQuoteRequest
            {
                InsuredName = txtInsuredName.Text.Trim(),
                RiskState = ddlRiskState.SelectedValue,
                VehYear = txtVehYear.Text.Trim(),
                VehMake = txtVehMake.Text.Trim(),
                VehModel = txtVehModel.Text.Trim(),
                // Passed through with its thousands separator intact. The modern
                // adapter parses it, so this page does not need to change.
                Mileage = txtMileage.Text.Trim(),
                PriorCovMonths = txtPriorCovMonths.Text.Trim()
            };

            var correlationId = ViewState[CorrelationIdKey] as string;

            // ---- THE SEAM. One call. No business logic on this side of it. ----
            var response = _apiClient.SubmitQuote(request, correlationId);

            if (response.IsAccepted)
            {
                ShowAccepted(response);
                return;
            }

            ShowFailure(response);
        }

        private void ShowAccepted(LegacyQuoteResponse response)
        {
            lblQuoteNbr.Text = Server.HtmlEncode(response.QuoteNbr);
            lblQuoteStatus.Text = Server.HtmlEncode(response.QuoteStatus);
            lblCorrelationId.Text = Server.HtmlEncode(response.CorrelationId);

            // Query string, not the GUID: the legacy status page polls by quote
            // number because that is all the legacy database ever stored.
            lnkStatus.NavigateUrl = "~/QuoteStatus.aspx?quoteNbr="
                                    + Server.UrlEncode(response.QuoteNbr);

            pnlResult.Visible = true;

            // Disabled to discourage an accidental second submission. Note this is
            // only a courtesy, NOT a correctness guarantee: the user can still
            // re-post, and a second quote would simply be a second quote, which is
            // harmless. Where duplicate submission would genuinely be dangerous —
            // taking a payment — the modern API enforces idempotency with a unique
            // database constraint rather than relying on a disabled button.
            btnSubmit.Enabled = false;
        }

        private void ShowFailure(LegacyQuoteResponse response)
        {
            // The API returns messages already uppercased in the legacy house
            // style. HtmlEncode regardless: never write a server response into a
            // Label without encoding, or a hostile value becomes stored XSS.
            lblServiceError.Text = Server.HtmlEncode(response.ReturnMessage);
            pnlServiceError.Visible = true;
            pnlResult.Visible = false;
        }

        /// <summary>Resets the form and issues a fresh correlation id.</summary>
        protected void btnClear_Click(object sender, EventArgs e)
        {
            txtInsuredName.Text = string.Empty;
            txtVehMake.Text = string.Empty;
            txtVehModel.Text = string.Empty;
            txtVehYear.Text = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
            txtMileage.Text = "12,000";
            txtPriorCovMonths.Text = "36";
            ddlRiskState.SelectedValue = "GA";

            pnlResult.Visible = false;
            pnlServiceError.Visible = false;
            btnSubmit.Enabled = true;

            // A new logical operation gets a new correlation id, so two quotes are
            // never conflated in the logs.
            ViewState[CorrelationIdKey] = Guid.NewGuid().ToString();
        }
    }
}
