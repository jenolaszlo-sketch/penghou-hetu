using Penghou.Hetu;

namespace Penghou.Hetu.CSharp.Tests;

public sealed class CSharpPublicApiContractTests
{
    [Fact]
    public void CSharp_PublicTypeSnapshot_IsIntentional()
    {
        var actual = typeof(CSharpCodeGraphPlugin).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "Penghou.Hetu.CSharp.HetuHostBuilderExtensions",
            "Penghou.Hetu.CSharpCodeGraphPlugin"
        }.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }
}
