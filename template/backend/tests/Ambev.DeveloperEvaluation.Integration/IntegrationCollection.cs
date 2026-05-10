using Xunit;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// xUnit collection that shares one <see cref="SalesApiFactory"/> (and
/// therefore one Postgres testcontainer) across every integration test
/// class. Tests in the same collection are NOT run in parallel, so per-test
/// database resets in <see cref="SalesApiFactory.ResetDatabaseAsync"/> are
/// safe to do without coordination.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<SalesApiFactory>
{
    public const string Name = "integration";
}
