using System;
using System.Configuration;
using System.Web.UI;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Master page code-behind.
    /// </summary>
    /// <remarks>
    /// Displays which API this build is pointed at. Trivial, but genuinely useful
    /// during a phased migration: when the same legacy binary runs against a
    /// local, test and production API, "which one am I looking at?" is a question
    /// somebody asks every day.
    /// </remarks>
    public partial class SiteMaster : MasterPage
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            var apiBase = ConfigurationManager.AppSettings["PolicyFlow.ApiBaseAddress"];

            litApiBase.Text = Server.HtmlEncode(
                string.IsNullOrEmpty(apiBase) ? "(not configured)" : apiBase);
        }
    }
}
