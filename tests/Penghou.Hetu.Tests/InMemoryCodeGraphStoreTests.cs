using Penghou.Hetu.Testing;

namespace Penghou.Hetu.Tests;

public sealed class InMemoryCodeGraphStoreTests
{
    [Fact]
    public async Task Store_PassesProviderConformanceSuite()
    {
        var report = await CodeGraphStoreConformanceSuite.VerifyAsync(
            new Fixture(),
            CancellationToken.None);

        Assert.Contains("equivalent-replacement-idempotent", report.PassedChecks);
        Assert.Contains("owned-deletion-and-shared-node-survival", report.PassedChecks);
        Assert.Contains("failed-replacement-atomic", report.PassedChecks);
        Assert.Contains("bounded-deterministic-traversal", report.PassedChecks);
        Assert.Contains("successful-source-state-round-trip", report.PassedChecks);
        Assert.Contains("failed-run-retains-source-state", report.PassedChecks);
    }

    private sealed class Fixture : ICodeGraphStoreFixture
    {
        public ICodeGraphStore CreateStore() => new InMemoryCodeGraphStore();
    }
}
