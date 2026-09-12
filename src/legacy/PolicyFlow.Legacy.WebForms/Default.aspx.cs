using System;
using System.Web.UI;
using PolicyFlow.Legacy.WebForms.Services;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Landing page. Reports whether the modern API is reachable.
    /// </summary>
    /// <remarks>
    /// A deliberately small piece of operational value: during a phased migration
    /// the most common support question is "is the new API up?", and answering it
    /// on the legacy home page saves a round trip to an operations team.
    /// </remarks>
    public partial class _Default : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (IsPostBack)
            {
                return;
            }

            // A read-only status probe, so a failure here degrades the page rather
            // than breaking it. The portal is still usable if this label is
            // pessimistic — the quote pages surface their own errors.
            try
            {
                var client = new PolicyFlowApiClient();
                var probe = client.GetQuoteStatus("Q-0000-000000", Guid.NewGuid().ToString());

                // ReturnCode 1 means "quote not found", which is the expected
                // answer for a deliberately bogus number and proves the API
                // answered. ReturnCode 2 means the transport itself failed.
                if (probe.ReturnCode == 2)
                {
                    lblApiStatus.Text = "UNAVAILABLE";
                    lblApiStatus.CssClass = "legacy-value-strong legacy-status-bad";
                    lblCarrierCount.Text = "unknown";
                    return;
                }

                lblApiStatus.Text = "AVAILABLE";
                lblApiStatus.CssClass = "legacy-value-strong legacy-status-good";
                lblCarrierCount.Text = "5 carriers, 3 coverage tiers each";
            }
            catch (Exception)
            {
                // Never let a status probe take the home page down.
                lblApiStatus.Text = "UNKNOWN";
                lblApiStatus.CssClass = "legacy-value-strong";
                lblCarrierCount.Text = "unknown";
            }
        }
    }
}
