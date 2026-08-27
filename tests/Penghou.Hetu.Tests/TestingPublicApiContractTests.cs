using Penghou.Hetu.Testing;

namespace Penghou.Hetu.Tests;

public sealed class TestingPublicApiContractTests
{
    [Fact]
    public void Testing_PublicTypeSnapshot_IsIntentional()
    {
        var actual = typeof(CodeGraphStoreConformanceSuite).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "Penghou.Hetu.Testing.CodeGraphStoreConformanceException",
            "Penghou.Hetu.Testing.CodeGraphStoreConformanceReport",
            "Penghou.Hetu.Testing.CodeGraphStoreConformanceSuite",
            "Penghou.Hetu.Testing.ICodeGraphStoreFixture"
        };

        Assert.Equal(expected, actual);
    }
}
