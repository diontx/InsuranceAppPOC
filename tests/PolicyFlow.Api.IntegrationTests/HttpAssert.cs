using System.Net;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// Status-code assertions that include the response body in the failure message.
/// </summary>
/// <remarks>
/// A bare <c>Assert.Equal(Accepted, response.StatusCode)</c> that fails reports
/// only "expected Accepted, got InternalServerError", which says nothing about
/// why. Since every failure in this API returns a <c>ProblemDetails</c> body with
/// a title, detail and correlation id, including it turns a blind re-run into a
/// diagnosis.
/// </remarks>
internal static class HttpAssert
{
    public static async Task StatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = await SafeReadAsync(response);

        Assert.Fail(
            $"Expected HTTP {(int)expected} {expected} but got {(int)response.StatusCode} " +
            $"{response.StatusCode} for {response.RequestMessage?.Method} " +
            $"{response.RequestMessage?.RequestUri?.PathAndQuery}.{Environment.NewLine}" +
            $"Response body:{Environment.NewLine}{body}");
    }

    /// <summary>Asserts the status is one of several acceptable codes.</summary>
    public static async Task StatusOneOfAsync(HttpResponseMessage response, params HttpStatusCode[] expected)
    {
        if (expected.Contains(response.StatusCode))
        {
            return;
        }

        var body = await SafeReadAsync(response);
        var expectedList = string.Join(" or ", expected.Select(e => $"{(int)e} {e}"));

        Assert.Fail(
            $"Expected HTTP {expectedList} but got {(int)response.StatusCode} " +
            $"{response.StatusCode}.{Environment.NewLine}Response body:{Environment.NewLine}{body}");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(body) ? "(empty)" : body;
        }
        catch (Exception ex)
        {
            return $"(could not read body: {ex.Message})";
        }
    }
}
