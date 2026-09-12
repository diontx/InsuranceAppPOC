using System;
using System.Globalization;
using System.Web.UI;
using PolicyFlow.Legacy.WebForms.Models;
using PolicyFlow.Legacy.WebForms.Services;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Code-behind for the legacy quote status page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Polls the modern API until underwriting reaches a terminal decision, using
    /// an <c>UpdatePanel</c> and <c>Timer</c> — the Web Forms partial-postback
    /// model. The logic mirrors the React SPA's polling hook exactly: tick,
    /// re-read status, stop at a terminal state, give up after a cap.
    /// </para>
    /// <para>
    /// Comparing the two implementations is the clearest argument in this
    /// repository for the rewrite of the <i>presentation</i> layer. They do the
    /// same thing; this one does it by re-running an entire page lifecycle and
    /// round-tripping ViewState every two seconds, and the SPA does it with one
    /// fetch of one indexed row.
    /// </para>
    /// <para>
    /// Note what is <b>not</b> here: no premium arithmetic, no status rules, no
    /// SQL. The API decides; this page renders.
    /// </para>
    /// </remarks>
    public partial class QuoteStatus : Page
    {
        private readonly PolicyFlowApiClient _apiClient = new PolicyFlowApiClient();

        private const string CorrelationIdKey = "PolicyFlow.CorrelationId";
        private const string PollCountKey = "PolicyFlow.PollCount";

        /// <summary>
        /// Poll cap. At 2s this is 60 seconds. Underwriting normally finishes in
        /// one or two, so hitting this means something is genuinely wrong — the
        /// worker is down, or the outbox is not draining. Without a cap the timer
        /// would postback forever on an abandoned browser tab, which is a real
        /// load problem when it is a full page lifecycle each time.
        /// </summary>
        private const int MaxPolls = 30;

        private int PollCount
        {
            get
            {
                var stored = ViewState[PollCountKey];
                return stored == null ? 0 : (int)stored;
            }
            set { ViewState[PollCountKey] = value; }
        }

        protected void Page_Load(object sender, EventArgs e)
        {
            if (IsPostBack)
            {
                return;
            }

            ViewState[CorrelationIdKey] = Guid.NewGuid().ToString();

            // Arriving from the quote entry page, which passes the quote number in
            // the query string. Looking it up immediately saves the user retyping
            // a number they just watched being generated.
            var quoteNbr = Request.QueryString["quoteNbr"];
            if (!string.IsNullOrEmpty(quoteNbr))
            {
                txtQuoteNbr.Text = quoteNbr;
                LookupAndRender();
            }
        }

        protected void btnLookup_Click(object sender, EventArgs e)
        {
            if (!Page.IsValid)
            {
                return;
            }

            // A manual lookup starts a fresh polling session.
            PollCount = 0;
            ViewState[CorrelationIdKey] = Guid.NewGuid().ToString();
            LookupAndRender();
        }

        /// <summary>
        /// Timer tick. Each one is a full ASP.NET postback, not a lightweight
        /// fetch, which is the cost the SPA rewrite removes.
        /// </summary>
        protected void pollTimer_Tick(object sender, EventArgs e)
        {
            PollCount = PollCount + 1;

            if (PollCount >= MaxPolls)
            {
                // Stop first, then report. Leaving the timer enabled after giving
                // up would keep posting back indefinitely.
                pollTimer.Enabled = false;
                lblPollingIndicator.Visible = false;

                ShowError(
                    "UNDERWRITING DID NOT COMPLETE AFTER " + MaxPolls +
                    " CHECKS. USE LOOKUP TO RETRY.");
                return;
            }

            LookupAndRender();
        }

        private void LookupAndRender()
        {
            var quoteNbr = txtQuoteNbr.Text.Trim();
            var correlationId = ViewState[CorrelationIdKey] as string;

            // ---- THE SEAM. The API owns the decision; this page renders it. ----
            var status = _apiClient.GetQuoteStatus(quoteNbr, correlationId);

            if (!status.IsFound)
            {
                pollTimer.Enabled = false;
                lblPollingIndicator.Visible = false;
                ShowError(status.ReturnMessage);
                return;
            }

            Render(status);
        }

        private void Render(LegacyQuoteStatusResponse status)
        {
            pnlError.Visible = false;
            pnlStatus.Visible = true;

            // Server.HtmlEncode on everything from the API. The values here are
            // ours, but encoding server output into a Label is unconditional: the
            // one place it is skipped is the one that becomes stored XSS.
            lblQuoteNbrResult.Text = Server.HtmlEncode(status.QuoteNbr);
            lblStatus.Text = Server.HtmlEncode(status.QuoteStatus);
            lblBestCarrier.Text = Server.HtmlEncode(status.BestCarrier ?? "-");
            lblOfferCnt.Text = status.OfferCnt.ToString(CultureInfo.InvariantCulture);
            lblCorrelationId.Text = Server.HtmlEncode(status.CorrelationId ?? "-");
            lblPollCount.Text = PollCount.ToString(CultureInfo.InvariantCulture);

            // Parsed with the invariant culture, then formatted for display with
            // the current one. Parsing with the current culture would read
            // "1482.50" as 148250 on a machine using a comma decimal separator.
            var premium = status.ParsedPremium;
            lblAnnualPrem.Text = premium.HasValue
                ? premium.Value.ToString("C2", CultureInfo.CurrentCulture)
                : "-";

            var hasDecisionMessage = !string.IsNullOrEmpty(status.DecisionMsg);
            rowDecision.Visible = hasDecisionMessage;
            if (hasDecisionMessage)
            {
                lblDecisionMsg.Text = Server.HtmlEncode(status.DecisionMsg);
            }

            pnlRatedNote.Visible = status.QuoteStatus == "RATED";

            // The stopping rule, matching the SPA: keep polling only while the
            // quote is non-terminal.
            var keepPolling = !status.IsTerminal && PollCount < MaxPolls;
            pollTimer.Enabled = keepPolling;
            lblPollingIndicator.Visible = keepPolling;
        }

        private void ShowError(string message)
        {
            lblError.Text = Server.HtmlEncode(message);
            pnlError.Visible = true;
            pnlStatus.Visible = false;
        }
    }
}
