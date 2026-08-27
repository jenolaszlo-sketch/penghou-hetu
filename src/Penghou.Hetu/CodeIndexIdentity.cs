using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hetu;

/// <summary>
/// Identifies the exact plugin-versioned source state behind a successful
/// publication independently of its indexing attempt.
/// </summary>
public sealed record CodeIndexIdentity
{
    public CodeIndexIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Index identities cannot contain surrounding whitespace or control characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    internal static CodeIndexIdentity Create(
        CodeRepositoryId repositoryId,
        IReadOnlyList<CodeSourceManifest> sources,
        string? snapshotIdentity,
        bool isConsistentSnapshot)
    {
        var value = new StringBuilder()
            .Append(repositoryId.Value)
            .Append('\n')
            .Append(isConsistentSnapshot)
            .Append('\n')
            .Append(snapshotIdentity ?? string.Empty)
            .Append('\n');
        foreach (var source in sources
                     .OrderBy(item => item.PluginId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.SourcePath, StringComparer.Ordinal))
        {
            value.Append(source.PluginId.Value).Append('\t')
                .Append(source.PluginVersion).Append('\t')
                .Append(source.SourcePath).Append('\t')
                .Append(source.SourceHash).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()));
        return new CodeIndexIdentity(
            Convert.ToHexString(hash).ToLowerInvariant());
    }
}
