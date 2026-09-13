using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Web.UI;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Code-behind for the page that hosts the Angular underwriting workbench.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately almost empty. It resolves three URLs and checks the bundle
    /// exists. Every underwriting rule lives in the modern API, and every piece of
    /// interaction lives in the Angular module — this class is the adapter between
    /// the Web Forms shell and a client-side application, and nothing more.
    /// </para>
    /// <para>
    /// There is no <c>IsPostBack</c> branch because the page never posts back: the
    /// module talks to the API with fetch, not through the page lifecycle.
    /// </para>
    /// </remarks>
    public partial class Underwriting : Page
    {
        private const string ModuleVirtualDirectory = "~/Scripts/underwriting/";

        /// <summary>Rendered into <c>data-api-base</c> on the host element.</summary>
        protected string ApiBaseAddress { get; private set; }

        protected string ModuleScriptUrl { get; private set; }

        protected string ModuleStylesUrl { get; private set; }

        protected bool IsModuleBuilt { get; private set; }

        protected void Page_Load(object sender, EventArgs e)
        {
            ApiBaseAddress = (ConfigurationManager.AppSettings["PolicyFlow.ApiBaseAddress"]
                              ?? "http://localhost:8081").TrimEnd('/');

            var scriptPath = Server.MapPath(ModuleVirtualDirectory + "main.js");
            IsModuleBuilt = File.Exists(scriptPath);

            // Say plainly that the bundle is missing rather than rendering an empty
            // element and a script tag that 404s silently.
            pnlModuleMissing.Visible = !IsModuleBuilt;
            phModule.Visible = IsModuleBuilt;

            if (!IsModuleBuilt)
            {
                return;
            }

            // The Angular build uses fixed file names (outputHashing: none) so this
            // page can reference them. The file's write time becomes the cache key,
            // so a redeployed bundle is fetched fresh instead of served from a
            // browser cache that still holds last release's JavaScript.
            ModuleScriptUrl = VersionedUrl("main.js");
            ModuleStylesUrl = VersionedUrl("styles.css");
        }

        private string VersionedUrl(string fileName)
        {
            var physicalPath = Server.MapPath(ModuleVirtualDirectory + fileName);
            var version = File.Exists(physicalPath)
                ? File.GetLastWriteTimeUtc(physicalPath).Ticks.ToString(CultureInfo.InvariantCulture)
                : "0";

            return ResolveUrl(ModuleVirtualDirectory + fileName) + "?v=" + version;
        }
    }
}
