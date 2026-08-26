using Microsoft.Extensions.DependencyInjection;
using Penghou.Hetu;

namespace Penghou.Hetu.CSharp;

/// <summary>C#-specific extensions for <see cref="HetuHostBuilder"/>.</summary>
public static class HetuHostBuilderExtensions
{
    /// <summary>Adds the Roslyn-based C# extraction plugin.</summary>
    public static HetuHostBuilder AddCSharpPlugin(this HetuHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddPlugin(new CSharpCodeGraphPlugin());
    }
}