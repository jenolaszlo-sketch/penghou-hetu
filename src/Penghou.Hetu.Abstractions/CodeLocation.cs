namespace Penghou.Hetu;

/// <summary>A one-based, inclusive-start and exclusive-end source span.</summary>
public sealed record CodeLocation
{
    public CodeLocation(
        string path,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        Path = ContractValue.RelativePath(path, nameof(path));
        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine));
        if (startColumn < 1)
            throw new ArgumentOutOfRangeException(nameof(startColumn));
        if (endLine < startLine)
            throw new ArgumentOutOfRangeException(nameof(endLine));
        if (endColumn < 1 ||
            (endLine == startLine && endColumn < startColumn))
        {
            throw new ArgumentOutOfRangeException(nameof(endColumn));
        }

        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    public string Path { get; }
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }
}
