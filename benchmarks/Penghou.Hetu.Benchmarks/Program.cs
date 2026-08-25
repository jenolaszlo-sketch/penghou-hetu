using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Penghou.Hetu;
using System.Runtime.InteropServices;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
public class LadybugStoreBenchmarks
{
    private readonly CodeRepositoryId _repositoryId = new("repo:benchmark");
    private readonly CodeIndexRunId _publishedRunId = new("run:benchmark:published");
    private readonly CodeIndexRunId _stagingRunId = new("run:benchmark:staging");
    private readonly CodePluginId _pluginId = new("plugin:benchmark");
    private string _databasePath = null!;
    private LadybugCodeGraphStore _store = null!;
    private CodeIndexUnitReplacement _replacement = null!;
    private CodeNodeId _middleNodeId = null!;

    [Params(100, 1000)]
    public int NodeCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        LoadWindowsOpenSsl("libcrypto-3-x64.dll");
        LoadWindowsOpenSsl("libssl-3-x64.dll");
        _databasePath = Path.Combine(Path.GetTempPath(), $"hetu-benchmark-{Guid.NewGuid():N}");
        _store = new(_databasePath);
        var started = DateTimeOffset.UtcNow;
        await _store.UpsertRepositoryAsync(new(_repositoryId));
        await _store.StoreIndexRunAsync(new(_repositoryId, _publishedRunId, started, plugins: [_pluginId]));
        var published = CreateReplacement(NodeCount, _publishedRunId);
        _middleNodeId = published.Nodes[NodeCount / 2].Id;
        await _store.StageIndexUnitAsync(published);
        await _store.CompleteIndexRunAsync(
            new(_repositoryId, _publishedRunId, started, CodeIndexRunStatus.Completed, started.AddSeconds(1), [_pluginId]),
            new(_repositoryId, _publishedRunId, []));
        await _store.StoreIndexRunAsync(new(
            _repositoryId,
            _stagingRunId,
            started.AddSeconds(2),
            plugins: [_pluginId]));
        _replacement = CreateReplacement(NodeCount, _stagingRunId);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_databasePath))
            Directory.Delete(_databasePath, recursive: true);
    }

    [Benchmark]
    public ValueTask StageIndexUnit() => _store.StageIndexUnitAsync(_replacement);

    [Benchmark]
    public ValueTask<IReadOnlyList<CodeGraphNode>> ExactQualifiedNameLookup() =>
        _store.FindNodesByQualifiedNameAsync(_repositoryId, $"Benchmark.Node{NodeCount / 2}");

    [Benchmark]
    public ValueTask<CodeGraphTraversalResult> BoundedTraversal() =>
        _store.TraverseAsync(
            _repositoryId,
            new(_middleNodeId, CodeGraphDirection.Both, [CodeEdgeKinds.Calls], maxDepth: 4, maxNodes: 25, maxEdges: 50));

    [Benchmark]
    public async Task DeleteAndReinsertUnit()
    {
        await _store.StageIndexUnitDeletionAsync(_repositoryId, _stagingRunId, _pluginId, _replacement.Origin.IndexUnitId);
        await _store.StageIndexUnitAsync(_replacement);
    }

    [Benchmark]
    public LadybugCodeGraphStoreHealth ReopenAndCheckHealth()
    {
        _store.Dispose();
        _store = new(_databasePath);
        return _store.CheckHealth();
    }

    private CodeIndexUnitReplacement CreateReplacement(int count, CodeIndexRunId runId)
    {
        var nodes = Enumerable.Range(0, count).Select(index => new CodeGraphNode(
            new($"node:{index:D6}"),
            CodeNodeKinds.Callable,
            $"Node{index}",
            $"Benchmark.Node{index}",
            new($"symbol:{index:D6}"))).ToArray();
        var edges = Enumerable.Range(0, count - 1).Select(index => new CodeGraphEdge(
            new($"edge:{index:D6}"),
            nodes[index].Id,
            nodes[index + 1].Id,
            CodeEdgeKinds.Calls,
            new(CodeEvidenceKind.Semantic, "benchmark"))).ToArray();
        return new(
            new CodeFactOrigin(_repositoryId, _pluginId, "1.0.0", runId, new("unit:benchmark")),
            nodes,
            edges: edges);
    }

    private static void LoadWindowsOpenSsl(string fileName)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git", "mingw64", "bin", fileName);
        if (File.Exists(path))
            NativeLibrary.Load(path);
    }
}
