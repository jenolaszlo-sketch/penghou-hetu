namespace Penghou.Hetu;

/// <summary>
/// Describes one source input assigned to a plugin without prescribing the
/// plugin's atomic index-unit ownership.
/// </summary>
public sealed record CodeSourceManifest
{
    public CodeSourceManifest(
        CodePluginId pluginId,
        string pluginVersion,
        string sourcePath,
        string sourceHash)
    {
        PluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        PluginVersion = ValidateIdentifier(pluginVersion, nameof(pluginVersion));
        SourcePath = CodeRepositoryEntry.NormalizeRelativePath(sourcePath);
        SourceHash = ValidateIdentifier(sourceHash, nameof(sourceHash));
    }

    public CodePluginId PluginId { get; }
    public string PluginVersion { get; }
    public string SourcePath { get; }
    public string SourceHash { get; }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Values cannot have surrounding whitespace or contain control characters.",
                parameterName);
        }

        return value;
    }
}
