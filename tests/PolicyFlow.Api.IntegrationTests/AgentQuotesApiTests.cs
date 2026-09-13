using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// The agent workspace API: quotes attributed from the token, and
/// <c>GET /api/quotes/mine</c> reading them back, against real SQL Server.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AgentQuotesApiTests
{
    private readonly PolicyFlowApiFactory _factory;
    private readonly HttpClient _anonymous;

    public AgentQuotesApiTests(PolicyFlowApiFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient();
    }

    /// <summary>A unique agent per test, because the database is shared across the suite.</summary>
    private static string NewAgentId() => $"agent-{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> SignInAsAsync(string agentId)
    {
        var tokenResponse = await _anonymous.PostAsJsonAsync(
            "/api/auth/dev-token", new { role = "Agent", subject = agentId });
        await HttpAssert.StatusAsync(HttpStatusCode.OK, tokenResponse);

        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string customerName, int priorInsuranceMonths = 36)
    {
        var response = await client.PostAsJsonAsync("/api/quotes", new
        {
            customerName,
            state = "GA",
            vehicleYear = 2023,
            vehicleMake = "Toyota",
            vehicleModel = "Camry",
            annualMileage = 12_000,
            priorInsuranceMonths
        });
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quoteId").GetGuid();
    }

    [Fact]
    public async Task SubmissionWithAnAgentToken_IsAttributedToThatAgent()
    {
        var agentId = NewAgentId();
        var agent = await SignInAsAsync(agentId);

        var quoteId = await SubmitAsync(agent, "Dana Reyes");

        await _factory.WithDbAsync(async db =>
            Assert.Equal(agentId, (await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId)).SubmittedBy));
    }

    [Fact]
    public async Task AnonymousSubmission_IsNotAttributed()
    {
        var quoteId = await SubmitAsync(_anonymous, "Walk In");

        await _factory.WithDbAsync(async db =>
            Assert.Null((await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == quoteId)).SubmittedBy));
    }

    [Fact]
    public async Task Mine_ListsTheAgentsQuotes_NewestFirst_WithTheirCurrentStatus()
    {
        var agentId = NewAgentId();
        var agent = await SignInAsAsync(agentId);

        var rated = await SubmitAsync(agent, "Clean Risk");
        var referred = await SubmitAsync(agent, "No History", priorInsuranceMonths: 0);
        await _factory.UnderwriteAsync(rated);
        await _factory.UnderwriteAsync(referred);

        var response = await agent.GetAsync("/api/quotes/mine");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(agentId, body.GetProperty("submittedBy").GetString());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([referred, rated], items.Select(i => i.GetProperty("quoteId").GetGuid()));
        Assert.Equal("Referral", items[0].GetProperty("status").GetString());
        Assert.Equal("Rated", items[1].GetProperty("status").GetString());
        Assert.Equal(15, items[1].GetProperty("offerCount").GetInt32());
    }

    [Fact]
    public async Task Mine_NeverShowsAnotherAgentsQuotes()
    {
        var first = await SignInAsAsync(NewAgentId());
        var second = await SignInAsAsync(NewAgentId());
        var firstsQuote = await SubmitAsync(first, "First Agents Client");

        var body = await (await second.GetAsync("/api/quotes/mine")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(
            body.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("quoteId").GetGuid() == firstsQuote);
    }

    [Fact]
    public async Task Mine_WithoutAToken_Returns401_EvenWithAuthorizationRelaxed()
    {
        var response = await _anonymous.GetAsync("/api/quotes/mine");

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, response);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sign-in required", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mine_WithAnInvalidToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, await client.GetAsync("/api/quotes/mine"));
    }
}
