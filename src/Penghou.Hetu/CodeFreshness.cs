namespace Penghou.Hetu;

/// <summary>Freshness of a published graph against live repository sources.</summary>
public enum CodeFreshnessStatus
{
    Unknown = 0,
    Fresh = 1,
    Stale = 2,
    SourceConflict = 3
}

/// <summary>
/// The result of comparing live repository sources with the latest published
/// source state. Read-only: checking freshness never stages, publishes, or
/// otherwise mutates the graph.
/// </summary>
public sealed record CodeFreshnessResult(
    CodeFreshnessStatus Status,
    CodeGraphPublication? Publication,
    int NewCount,
    int ChangedCount,
    int UnchangedCount,
    int DeletedCount);
