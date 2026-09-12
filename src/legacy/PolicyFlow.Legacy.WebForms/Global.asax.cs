using System;
using System.Web;

namespace PolicyFlow.Legacy.WebForms
{
    /// <summary>
    /// Application lifecycle hooks.
    /// </summary>
    /// <remarks>
    /// Kept minimal. In the original application this file had grown to include
    /// a static connection pool, a cached rating table and several static mutable
    /// fields — all of it now gone, because the state it held belonged to logic
    /// that has moved to the modern API.
    /// </remarks>
    public class Global : HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            // Nothing to initialise. The application is now a thin UI over the
            // modern API and holds no caches, no connections and no state.
        }

        /// <summary>
        /// Last-resort handler for an unhandled exception.
        /// </summary>
        /// <remarks>
        /// Clears the error and redirects rather than letting ASP.NET render its
        /// default error page. customErrors in Web.config covers the normal path;
        /// this is the belt to that braces, because an exception escaping to the
        /// default handler can expose a stack trace and file paths.
        /// </remarks>
        protected void Application_Error(object sender, EventArgs e)
        {
            var exception = Server.GetLastError();
            if (exception == null)
            {
                return;
            }

            // A real deployment would write to the Windows event log or a log
            // aggregator here. Trace is used to avoid adding a logging dependency
            // to a project that is scheduled for retirement.
            System.Diagnostics.Trace.TraceError(
                "Unhandled exception in legacy portal: {0}", exception);

            Server.ClearError();
            Response.Redirect("~/Default.aspx", false);
        }
    }
}
