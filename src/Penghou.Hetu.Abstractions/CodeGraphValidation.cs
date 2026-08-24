namespace Penghou.Hetu;

public enum CodeGraphValidationErrorKind
{
    InvalidIdentity = 0,
    InvalidProperty = 1,
    LimitExceeded = 2,
    DuplicateFact = 3,
    MissingEndpoint = 4,
    OwnershipMismatch = 5,
    IncompleteIndexUnit = 6
}

/// <summary>A bounded, source-content-free graph contract violation.</summary>
public sealed record CodeGraphValidationError(
    CodeGraphValidationErrorKind Kind,
    string Code,
    string Message,
    string? FactId = null);

/// <summary>Raised when a sink rejects a graph batch before commit.</summary>
public sealed class CodeGraphBatchRejectedException : Exception
{
    public CodeGraphBatchRejectedException(
        string message,
        IReadOnlyList<CodeGraphValidationError> errors)
        : base(message)
    {
        Errors = errors?.ToArray() ??
            throw new ArgumentNullException(nameof(errors));
    }

    public IReadOnlyList<CodeGraphValidationError> Errors { get; }
}
