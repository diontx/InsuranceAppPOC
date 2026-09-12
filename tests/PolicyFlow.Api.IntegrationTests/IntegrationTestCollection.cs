namespace PolicyFlow.Api.IntegrationTests;

/// <summary>
/// Puts every integration test class in one xUnit collection sharing a single
/// <see cref="PolicyFlowApiFactory"/>.
/// </summary>
/// <remarks>
/// <para>Two reasons this is a shared collection rather than per-class fixtures.</para>
/// <para>
/// <b>Correctness.</b> The factory publishes its connection string as a process
/// environment variable, because the top-level <c>Program.cs</c> reads
/// configuration during service registration and nothing else is applied early
/// enough. Environment variables are process-global, so two factories would
/// overwrite each other's connection string and tests would silently run against
/// the wrong database.
/// </para>
/// <para>
/// <b>Cost.</b> One database is created and migrated per run instead of one per
/// test class. Tests are written to be data-independent — each creates its own
/// quote and asserts only on rows it owns — so sharing the database is safe.
/// </para>
/// <para>
/// xUnit runs classes within a collection sequentially, which also removes any
/// chance of two classes racing on the shared schema.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<PolicyFlowApiFactory>
{
    public const string Name = "PolicyFlow integration tests";
}
