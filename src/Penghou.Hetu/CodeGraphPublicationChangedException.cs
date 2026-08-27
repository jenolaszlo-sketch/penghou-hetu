namespace Penghou.Hetu;

/// <summary>
/// Indicates that a publication-bound query observed a different successful
/// publication instead of silently combining graph generations.
/// </summary>
public sealed class CodeGraphPublicationChangedException : InvalidOperationException
{
    public CodeGraphPublicationChangedException(
        CodeGraphPublication expected,
        CodeGraphPublication? actual)
        : base(CreateMessage(expected, actual))
    {
        Expected = expected ?? throw new ArgumentNullException(nameof(expected));
        Actual = actual;
    }

    public CodeGraphPublication Expected { get; }

    public CodeGraphPublication? Actual { get; }

    private static string CreateMessage(
        CodeGraphPublication expected,
        CodeGraphPublication? actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return actual is null
            ? $"Repository '{expected.RepositoryId.Value}' no longer has a queryable publication."
            : $"Repository '{expected.RepositoryId.Value}' moved from publication " +
              $"'{expected.IndexRunId.Value}' to '{actual.IndexRunId.Value}'.";
    }
}
