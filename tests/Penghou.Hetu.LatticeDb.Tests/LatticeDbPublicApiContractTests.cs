using Penghou.Hetu;

namespace Penghou.Hetu.LatticeDb.Tests;

public sealed class LatticeDbPublicApiContractTests
{
    [Fact]
    public void LatticeDb_PublicTypeSnapshot_IsIntentional()
    {
        var actual = typeof(LatticeCodeGraphStore).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "Penghou.Hetu.LatticeCodeGraphSchemaException",
            "Penghou.Hetu.LatticeCodeGraphStore",
            "Penghou.Hetu.LatticeDb.HetuHostBuilderExtensions",
            "Penghou.Hetu.LatticeDb.LatticeDbStoreOptions"
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }
}
