using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using PolicyFlow.Legacy.WebForms.Models;

namespace PolicyFlow.Legacy.WebForms.Services
{
    /// <summary>
    /// The legacy side of the modernization seam: calls the modern REST API
    /// instead of running business logic locally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is what the whole migration turns on. <b>Before</b>
    /// modernization, the rating rules lived in <c>QuoteEntry.aspx.cs</c>
    /// code-behind — a few hundred lines of premium arithmetic interleaved with
    /// <c>txtMileage.Text</c> reads and <c>lblPremium.Text</c> writes, untestable
    /// because it could not be instantiated without a page lifecycle.
    /// <b>After</b>, the code-behind gathers input, calls this client, and renders
    /// the result. Not one rating rule remains in this project.
    /// </para>
    /// <para>
    /// <b>Why synchronous <see cref="WebRequest"/> and not <c>HttpClient</c> with
    /// async/await.</b> Deliberately. Web Forms pages can use async through
    /// <c>RegisterAsyncTask</c>, but retrofitting it across an existing page
    /// lifecycle is invasive and risks subtle ordering bugs in pages nobody has
    /// touched in years. The pragmatic modernization choice is to leave the legacy
    /// application's threading model alone and move the <i>logic</i> out. The
    /// modern API is where async/await matters, and it already uses it throughout.
    /// This is a transitional shim that gets deleted with the project.
    /// </para>
    /// <para>
    /// <b>Configuration, not code.</b> The base address comes from
    /// <c>Web.config</c> so the same build points at local Docker, a test
    /// environment or production without recompiling.
    /// </para>
    /// </remarks>
    public class PolicyFlowApiClient
    {
        private readonly string _baseAddress;
        private readonly int _timeoutMs;

        public PolicyFlowApiClient()
        {
            // appSettings, not a hard-coded URL, so environments differ by config.
            _baseAddress = (ConfigurationManager.AppSettings["PolicyFlow.ApiBaseAddress"]
                            ?? "http://localhost:8081").TrimEnd('/');

            int timeout;
            _timeoutMs = int.TryParse(
                ConfigurationManager.AppSettings["PolicyFlow.ApiTimeoutMs"], out timeout)
                ? timeout
                : 15000;
        }

        /// <summary>
        /// Submits a quote through the compatibility endpoint.
        /// </summary>
        /// <remarks>
        /// Posts the legacy contract unchanged. The API translates it into the
        /// modern command and returns the legacy response shape, so this method
        /// has no mapping logic of its own and no business rules.
        /// </remarks>
        public LegacyQuoteResponse SubmitQuote(LegacyQuoteRequest request, string correlationId)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            try
            {
                return Post<LegacyQuoteRequest, LegacyQuoteResponse>(
                    "/legacy/quotes", request, correlationId);
            }
            catch (WebException ex)
            {
                // ReturnCode 2 is the legacy convention for a system error, as
                // distinct from 1 for a validation failure. The pages already
                // distinguish them, so preserving the distinction matters.
                return new LegacyQuoteResponse
                {
                    ReturnCode = 2,
                    ReturnMessage = "QUOTE SERVICE UNAVAILABLE: " + Describe(ex)
                };
            }
        }

        /// <summary>
        /// Polls quote status by quote number.
        /// </summary>
        /// <remarks>
        /// By quote number, not GUID, because the legacy database only ever stored
        /// the printed number. The modern API keeps a by-number route specifically
        /// so this access path survives; requiring the GUID would have forced a
        /// schema change on the legacy side.
        /// </remarks>
        public LegacyQuoteStatusResponse GetQuoteStatus(string quoteNbr, string correlationId)
        {
            if (string.IsNullOrEmpty(quoteNbr))
            {
                return new LegacyQuoteStatusResponse
                {
                    ReturnCode = 1,
                    ReturnMessage = "QUOTE NUMBER IS REQUIRED"
                };
            }

            try
            {
                return Get<LegacyQuoteStatusResponse>(
                    "/legacy/quotes/" + Uri.EscapeDataString(quoteNbr), correlationId);
            }
            catch (WebException ex)
            {
                return new LegacyQuoteStatusResponse
                {
                    ReturnCode = 2,
                    ReturnMessage = "STATUS SERVICE UNAVAILABLE: " + Describe(ex)
                };
            }
        }

        // ------------------------------------------------------------------
        // Transport
        // ------------------------------------------------------------------

        private TResponse Post<TRequest, TResponse>(
            string path, TRequest body, string correlationId)
        {
            var httpRequest = CreateRequest(path, "POST", correlationId);
            httpRequest.ContentType = "application/json";

            var serializer = new DataContractJsonSerializer(typeof(TRequest));

            using (var stream = httpRequest.GetRequestStream())
            {
                serializer.WriteObject(stream, body);
            }

            return ReadResponse<TResponse>(httpRequest);
        }

        private TResponse Get<TResponse>(string path, string correlationId)
        {
            var httpRequest = CreateRequest(path, "GET", correlationId);
            return ReadResponse<TResponse>(httpRequest);
        }

        private HttpWebRequest CreateRequest(string path, string method, string correlationId)
        {
            var httpRequest = (HttpWebRequest)WebRequest.Create(_baseAddress + path);
            httpRequest.Method = method;
            httpRequest.Accept = "application/json";
            httpRequest.Timeout = _timeoutMs;
            httpRequest.ReadWriteTimeout = _timeoutMs;

            // Propagating the correlation id is what makes a legacy-originated
            // quote traceable through the modern API, SQL, the queue and the
            // worker. Without it, legacy traffic would be the one blind spot in
            // an otherwise fully traced system.
            if (!string.IsNullOrEmpty(correlationId))
            {
                httpRequest.Headers.Add("X-Correlation-Id", correlationId);
            }

            // TLS 1.2 must be requested explicitly. .NET Framework 4.8 honours the
            // OS default, and on an older server that can still mean TLS 1.0 —
            // which modern endpoints refuse, producing a confusing handshake
            // failure rather than a clear error.
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            return httpRequest;
        }

        private static TResponse ReadResponse<TResponse>(HttpWebRequest httpRequest)
        {
            using (var response = (HttpWebResponse)httpRequest.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    throw new WebException("The response carried no body.");
                }

                var serializer = new DataContractJsonSerializer(typeof(TResponse));
                return (TResponse)serializer.ReadObject(stream);
            }
        }

        /// <summary>
        /// Builds an operator-facing description of a transport failure.
        /// </summary>
        /// <remarks>
        /// Returns the status and a short reason only. The raw exception can carry
        /// the internal host name and path, which do not belong on a page a
        /// customer can see.
        /// </remarks>
        private static string Describe(WebException ex)
        {
            var httpResponse = ex.Response as HttpWebResponse;
            if (httpResponse != null)
            {
                return "HTTP " + (int)httpResponse.StatusCode;
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                    return "TIMEOUT";
                case WebExceptionStatus.ConnectFailure:
                    return "CONNECTION REFUSED";
                case WebExceptionStatus.NameResolutionFailure:
                    return "HOST NOT FOUND";
                default:
                    return ex.Status.ToString().ToUpperInvariant();
            }
        }
    }
}
