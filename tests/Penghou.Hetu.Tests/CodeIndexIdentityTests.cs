namespace Penghou.Hetu.Tests;

public sealed class CodeIndexIdentityTests
{
    private static readonly CodeRepositoryId RepositoryId = new("repo:identity");
    private static readonly CodePluginId PluginId = new("plugin:identity");

    [Fact]
    public void SameExactSourceState_HasSameIdentityAcrossRunsAndInputOrder()
    {
        CodeSourceManifest[] sources =
        [
            new(PluginId, "1.0.0", "src/B.cs", "sha256:b"),
            new(PluginId, "1.0.0", "src/A.cs", "sha256:a")
        ];

        var first = new CodeRepositoryIndexState(
            RepositoryId, new("run:first"), sources, "snapshot:one", true);
        var second = new CodeRepositoryIndexState(
            RepositoryId, new("run:second"), sources.Reverse().ToArray(), "snapshot:one", true);

        Assert.Equal(first.IndexIdentity, second.IndexIdentity);
    }

    [Fact]
    public void SourcePluginOrSnapshotChange_ChangesIdentity()
    {
        var baseline = State("1.0.0", "sha256:one", "snapshot:one");

        Assert.NotEqual(baseline.IndexIdentity,
            State("1.0.0", "sha256:two", "snapshot:one").IndexIdentity);
        Assert.NotEqual(baseline.IndexIdentity,
            State("2.0.0", "sha256:one", "snapshot:one").IndexIdentity);
        Assert.NotEqual(baseline.IndexIdentity,
            State("1.0.0", "sha256:one", "snapshot:two").IndexIdentity);
    }

    [Fact]
    public void SuppliedIdentity_MustMatchComputedSourceState()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new CodeRepositoryIndexState(
                RepositoryId,
                new("run:invalid"),
                [new(PluginId, "1.0.0", "src/A.cs", "sha256:one")],
                indexIdentity: new("invalid")));

        Assert.Equal("indexIdentity", exception.ParamName);
    }

    private static CodeRepositoryIndexState State(
        string pluginVersion,
        string sourceHash,
        string snapshotIdentity) =>
        new(
            RepositoryId,
            new("run:test"),
            [new(PluginId, pluginVersion, "src/A.cs", sourceHash)],
            snapshotIdentity,
            true);
}
