namespace Penghou.Hetu;

/// <summary>
/// Validates streamed plugin batches and commits each completed index unit as
/// one atomic store replacement.
/// </summary>
public sealed class CodeGraphIngestionSink : ICodeGraphSink, IAsyncDisposable
{
    private readonly ICodeGraphStore _store;
    private readonly CodeGraphBatchValidator _validator;
    private readonly Action<CodeGraphIngestionDiagnostics>? _onCompleted;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<OwnerKey, Accumulator> _pending = [];
    private readonly HashSet<OwnerKey> _rejected = [];
    private bool _disposed;

    public CodeGraphIngestionSink(
        ICodeGraphStore store,
        CodeGraphBatchLimits? limits = null,
        CodeGraphBatchValidator? validator = null,
        Action<CodeGraphIngestionDiagnostics>? onCompleted = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Limits = limits ?? new CodeGraphBatchLimits();
        _validator = validator ?? new CodeGraphBatchValidator();
        _onCompleted = onCompleted;
    }

    public CodeGraphBatchLimits Limits { get; }

    public async ValueTask WriteBatchAsync(
        CodeGraphBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var errors = _validator.Validate(batch, Limits);
            if (errors.Count > 0)
                throw Reject(batch.Origin, errors);

            var key = OwnerKey.From(batch.Origin);
            if (_rejected.Contains(key))
            {
                throw Reject(
                    batch.Origin,
                    [Error(
                        CodeGraphValidationErrorKind.IncompleteIndexUnit,
                        "ingestion.index-unit.rejected",
                        "The index unit was already rejected and must be restarted.")]);
            }

            if (!_pending.TryGetValue(key, out var pending))
            {
                pending = new Accumulator(batch.Origin);
                _pending.Add(key, pending);
            }
            else if (pending.Origin != batch.Origin)
            {
                Poison(key);
                throw Reject(
                    batch.Origin,
                    [Error(
                        CodeGraphValidationErrorKind.OwnershipMismatch,
                        "ingestion.origin.changed",
                        "Every batch in an index unit must have identical origin metadata.")]);
            }

            var prospectiveBatches = pending.BatchesReceived + 1;
            var prospectiveFacts = pending.FactCount +
                batch.Nodes.Count +
                batch.Declarations.Count +
                batch.Edges.Count;
            if (prospectiveBatches > Limits.MaxBatchesPerIndexUnit ||
                prospectiveFacts > Limits.MaxFactsPerIndexUnit)
            {
                Poison(key);
                throw Reject(
                    batch.Origin,
                    [Error(
                        CodeGraphValidationErrorKind.LimitExceeded,
                        "ingestion.index-unit.limit",
                        "The index unit exceeded its configured ingestion limit.")]);
            }

            pending.Append(batch);
            if (!batch.CompletesIndexUnit)
                return;

            _pending.Remove(key);
            var replacement = pending.ToReplacement();
            try
            {
                await _store.ReplaceIndexUnitAsync(replacement, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _rejected.Add(key);
                throw;
            }

            Report(pending.ToDiagnostics());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            _rejected.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private CodeGraphBatchRejectedException Reject(
        CodeFactOrigin origin,
        IReadOnlyList<CodeGraphValidationError> errors)
    {
        var key = OwnerKey.From(origin);
        _pending.TryGetValue(key, out var pending);
        _pending.Remove(key);
        _rejected.Add(key);
        Report(pending?.ToDiagnostics(
            errors.Count,
            errors.Select(error => error.Code)
                .Distinct()
                .Take(20)
                .ToArray()) ??
            new CodeGraphIngestionDiagnostics(
                origin.RepositoryId,
                origin.IndexRunId,
                origin.IndexUnitId,
                0,
                0,
                0,
                0,
                errors.Count,
                errors.Select(error => error.Code)
                    .Distinct()
                    .Take(20)
                    .ToArray()));
        return new CodeGraphBatchRejectedException(
            "The graph batch was rejected.",
            errors);
    }

    private void Report(CodeGraphIngestionDiagnostics diagnostics)
    {
        try
        {
            _onCompleted?.Invoke(diagnostics);
        }
        catch
        {
            // Diagnostics must never change ingestion success or failure.
        }
    }

    private void Poison(OwnerKey key)
    {
        _pending.Remove(key);
        _rejected.Add(key);
    }

    private static CodeGraphValidationError Error(
        CodeGraphValidationErrorKind kind,
        string code,
        string message) =>
        new(kind, code, message);

    private readonly record struct OwnerKey(
        string RepositoryId,
        string PluginId,
        string IndexUnitId)
    {
        public static OwnerKey From(CodeFactOrigin origin) =>
            new(
                origin.RepositoryId.Value,
                origin.PluginId.Value,
                origin.IndexUnitId.Value);
    }

    private sealed class Accumulator(CodeFactOrigin origin)
    {
        private readonly List<CodeGraphNode> _nodes = [];
        private readonly List<CodeGraphDeclaration> _declarations = [];
        private readonly List<CodeGraphEdge> _edges = [];

        public CodeFactOrigin Origin { get; } = origin;
        public int BatchesReceived { get; private set; }
        public int FactCount => _nodes.Count + _declarations.Count + _edges.Count;

        public void Append(CodeGraphBatch batch)
        {
            BatchesReceived++;
            _nodes.AddRange(batch.Nodes);
            _declarations.AddRange(batch.Declarations);
            _edges.AddRange(batch.Edges);
        }

        public CodeIndexUnitReplacement ToReplacement() =>
            new(Origin, _nodes, _declarations, _edges);

        public CodeGraphIngestionDiagnostics ToDiagnostics(
            int rejectedFacts = 0,
            IReadOnlyList<string>? warningCodes = null) =>
            new(
                Origin.RepositoryId,
                Origin.IndexRunId,
                Origin.IndexUnitId,
                BatchesReceived,
                _nodes.Count,
                _declarations.Count,
                _edges.Count,
                rejectedFacts,
                warningCodes ?? []);
    }
}
