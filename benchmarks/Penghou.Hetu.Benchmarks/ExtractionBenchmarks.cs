using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using Penghou.Hetu;

namespace Penghou.Hetu.Benchmarks;

/// <summary>Measures bounded C# extraction throughput over in-memory sources.</summary>
[MemoryDiagnoser]
public class ExtractionBenchmarks
{
    private readonly CodeRepositoryId _repositoryId = new("repo:extraction-benchmark");
    private CodeGraphSource[] _sources = null!;

    [Params(100, 1000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sources = Enumerable.Range(0, FileCount).Select(CreateSource).ToArray();
    }

    [Benchmark]
    public async Task<int> ExtractSources()
    {
        var context = new CodeGraphPluginContext(
            _repositoryId,
            "memory://extraction-benchmark",
            new CodeIndexRunId("run:extraction-benchmark"),
            _sources);
        var sink = new CountingSink();
        var plugin = new CSharpCodeGraphPlugin();
        await using var session = await plugin.CreateSessionAsync(context);
        await session.ExtractAsync(sink);
        return sink.NodeCount;
    }

    private static CodeGraphSource CreateSource(int index)
    {
        var content = $$"""
            namespace Benchmark;

            public sealed class Type{{index}}
            {
                public int Value { get; private set; }

                public Type{{index}}(int value) => Value = value;

                public int Add(int amount) => Value + amount;

                public string Describe() => $"Type{{index}}:{Value}";
            }
            """;
        var hash = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new(
            $"src/Type{index}.cs",
            hash,
            _ => new ValueTask<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(content))));
    }

    private sealed class CountingSink : ICodeGraphSink
    {
        public CodeGraphBatchLimits Limits { get; } = new();

        public int NodeCount { get; private set; }

        public ValueTask WriteBatchAsync(
            CodeGraphBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NodeCount += batch.Nodes.Count;
            return ValueTask.CompletedTask;
        }
    }
}
