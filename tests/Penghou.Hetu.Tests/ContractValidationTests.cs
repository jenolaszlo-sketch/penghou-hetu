using Penghou.Hetu;

namespace Penghou.Hetu.Tests;

public sealed class ContractValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("line\nbreak")]
    public void Identities_RejectMalformedValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new CodeNodeId(value));
    }

    [Theory]
    [InlineData("/absolute/file.cs")]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    public void Location_RejectsPathsOutsideRepository(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new CodeLocation(path, 1, 1, 1, 2));
    }

    [Fact]
    public void Location_NormalizesDirectorySeparators()
    {
        var location = new CodeLocation(
            "src\\Example.cs",
            1,
            1,
            2,
            1);

        Assert.Equal("src/Example.cs", location.Path);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NumberProperty_RejectsNonFiniteValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CodeNumberProperty(value));
    }

    [Fact]
    public void Node_RejectsNullPropertyValues()
    {
        var properties = new Dictionary<string, CodePropertyValue>
        {
            ["invalid"] = null!
        };

        Assert.Throws<ArgumentException>(() =>
            new CodeGraphNode(
                new CodeNodeId("node:one"),
                CodeNodeKinds.Type,
                "Example",
                properties: properties));
    }

    [Fact]
    public void Evidence_RejectsConfidenceForDeterministicFacts()
    {
        Assert.Throws<ArgumentException>(() =>
            new CodeEvidence(
                CodeEvidenceKind.Semantic,
                "roslyn",
                confidence: 0.99));
    }

    [Fact]
    public void Evidence_AllowsBoundedConfidenceForHeuristics()
    {
        var evidence = new CodeEvidence(
            CodeEvidenceKind.Heuristic,
            "hetu-test",
            confidence: 0.75);

        Assert.Equal(0.75, evidence.Confidence);
    }

    [Fact]
    public void Origin_RequiresSourcePathAndHashTogether()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateOrigin(sourcePath: "src/Example.cs"));
    }

    [Fact]
    public void BatchLimits_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CodeGraphBatchLimits(maxEdges: 0));
    }

    [Fact]
    public void Kinds_AllowNamespacedConsumerExtensions()
    {
        var customNodeKind = new CodeNodeKind("solo:test-fixture");
        var customEdgeKind = new CodeEdgeKind("solo:covered-by-test");

        Assert.Equal("solo:test-fixture", customNodeKind.Value);
        Assert.Equal("solo:covered-by-test", customEdgeKind.Value);
    }

    private static CodeFactOrigin CreateOrigin(
        string? sourcePath = null,
        string? sourceHash = null) =>
        new(
            new CodeRepositoryId("repo:test"),
            new CodePluginId("plugin:test"),
            "1.0.0",
            new CodeIndexRunId("run:one"),
            new CodeIndexUnitId("unit:one"),
            sourcePath,
            sourceHash);
}
