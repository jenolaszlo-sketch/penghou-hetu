using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hetu;

/// <summary>
/// Shared durable command-log engine for providers that persist Hetu mutations
/// as an append-style record log and rebuild query state by replay.
/// </summary>
/// <remarks>
/// This centralizes the trickiest provider logic (log folding, staged-run
/// publication, replay ordering) so every durable provider shares one
/// implementation. Providers only implement record-table reads and writes;
/// all graph semantics stay in <see cref="InMemoryCodeGraphStore"/>.
/// </remarks>
internal static class DurableCommandLog
{
    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    internal static string Serialize<T>(T value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));

    internal static string RunKey(CodeIndexRunManifest run) =>
        $"{run.RepositoryId.Value}\n{run.Id.Value}";

    internal static string UnitKey(CodeFactOrigin origin) =>
        UnitKey(origin.RepositoryId, origin.PluginId, origin.IndexUnitId);

    internal static string UnitKey(
        CodeRepositoryId repositoryId,
        CodePluginId pluginId,
        CodeIndexUnitId unitId) =>
        $"{repositoryId.Value}\n{pluginId.Value}\n{unitId.Value}";

    internal static string StageKey(PersistedCommand command)
    {
        var origin = command.Replacement?.Origin;
        return $"{origin?.RepositoryId.Value ?? command.RepositoryId!.Value}\n{origin?.IndexRunId.Value ?? command.RunId!.Value}\n{origin?.PluginId.Value ?? command.PluginId!.Value}\n{origin?.IndexUnitId.Value ?? command.UnitId!.Value}";
    }

    internal static List<PersistedCommand> Apply(
        IReadOnlyList<PersistedCommand> current,
        PersistedCommand command)
    {
        if (command.Kind == "complete")
        {
            var publishedNext = current.Where(existing => !MatchesRun(existing, command.Run!)).ToList();
            foreach (var staged in current.Where(existing => MatchesRun(existing, command.Run!)))
            {
                var published = staged.Kind == "stage-replace"
                    ? new PersistedCommand("replace", Replacement: staged.Replacement)
                    : new PersistedCommand("delete", RepositoryId: staged.RepositoryId, PluginId: staged.PluginId, UnitId: staged.UnitId);
                publishedNext = Apply(publishedNext, published);
            }
            publishedNext = publishedNext.Where(existing => !SameSlot(existing, command)).ToList();
            publishedNext.Add(command);
            return publishedNext;
        }

        var next = current.Where(existing => !SameSlot(existing, command)).ToList();
        if (command.Kind != "delete")
            next.Add(command);
        if (command.Kind == "run" && command.Run!.Status != CodeIndexRunStatus.Running)
            next.RemoveAll(existing => MatchesRun(existing, command.Run));
        return next;
    }

    internal static bool SameSlot(PersistedCommand existing, PersistedCommand command) => command.Kind switch
    {
        "repository" => existing.Kind == "repository" && existing.Repository!.Id == command.Repository!.Id,
        "run" or "complete" => existing.Kind is "run" or "complete" && existing.Run!.RepositoryId == command.Run!.RepositoryId && existing.Run.Id == command.Run.Id,
        "replace" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.Replacement!.Origin),
        "delete" => existing.Kind == "replace" && UnitKey(existing.Replacement!.Origin) == UnitKey(command.RepositoryId!, command.PluginId!, command.UnitId!),
        "stage-replace" => existing.Kind is "stage-replace" or "stage-delete" && StageKey(existing) == StageKey(command),
        "stage-delete" => existing.Kind is "stage-replace" or "stage-delete" && StageKey(existing) == StageKey(command),
        _ => false
    };

    internal static bool MatchesRun(PersistedCommand command, CodeIndexRunManifest run) =>
        command.Kind is "stage-replace" or "stage-delete" &&
        (command.Replacement?.Origin.RepositoryId ?? command.RepositoryId) == run.RepositoryId &&
        (command.Replacement?.Origin.IndexRunId ?? command.RunId) == run.Id;

    internal static List<PersistedCommand> Reconstruct(
        IEnumerable<CodeRepositoryManifest> repositories,
        IEnumerable<CodeIndexRunManifest> runs,
        IEnumerable<CodeIndexUnitReplacement> units,
        IEnumerable<PersistedCommand> stages,
        IEnumerable<CodeRepositoryIndexState> states)
    {
        var statesByRepository = states.ToDictionary(
            state => state.RepositoryId.Value,
            StringComparer.Ordinal);
        var runList = runs.ToArray();
        var terminals = runList.Where(run => run.Status != CodeIndexRunStatus.Running)
            .Select(run => run.Status == CodeIndexRunStatus.Completed &&
                    statesByRepository.TryGetValue(run.RepositoryId.Value, out var state) && state.IndexRunId == run.Id
                ? new PersistedCommand("complete", Run: run, State: state)
                : new PersistedCommand("run", Run: run));
        var running = runList.Where(run => run.Status == CodeIndexRunStatus.Running)
            .Select(run => new PersistedCommand("run", Run: run));
        return repositories
            .Select(value => new PersistedCommand("repository", Repository: value))
            .Concat(running)
            .Concat(terminals)
            .Concat(units.Select(value => new PersistedCommand("replace", Replacement: value)))
            .Concat(stages)
            .ToList();
    }

    internal static async Task<InMemoryCodeGraphStore> ReplayAsync(
        IReadOnlyList<PersistedCommand> commands,
        CancellationToken cancellationToken)
    {
        var store = new InMemoryCodeGraphStore();
        foreach (var command in commands.Where(command => command.Kind == "repository"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.UpsertRepositoryAsync(command.Repository!, cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind is "run" or "complete"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = command.Run!;
            await store.StoreIndexRunAsync(
                run.Status == CodeIndexRunStatus.Running
                    ? run
                    : new CodeIndexRunManifest(run.RepositoryId, run.Id, run.StartedAt, plugins: run.Plugins),
                cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind == "replace"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.RestorePublishedIndexUnitAsync(command.Replacement!, cancellationToken);
        }
        foreach (var command in commands.Where(command => command.Kind is "stage-replace" or "stage-delete"))
            await ApplyCommandAsync(store, command, cancellationToken);
        foreach (var command in commands.Where(command => command.Kind is "run" or "complete" &&
                     command.Run!.Status != CodeIndexRunStatus.Running))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Kind == "complete")
                await store.RestoreCompletedRunAsync(command.Run!, command.State!, cancellationToken);
            else if (command.Run!.Status == CodeIndexRunStatus.Completed)
                await store.RestoreIndexRunAsync(command.Run, cancellationToken);
            else
                await store.StoreIndexRunAsync(command.Run!, cancellationToken);
        }
        store.RebaseRunningRuns();
        return store;
    }

    internal static async Task ApplyCommandAsync(
        InMemoryCodeGraphStore store,
        PersistedCommand command,
        CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case "repository": await store.UpsertRepositoryAsync(command.Repository!, cancellationToken); break;
            case "run": await store.StoreIndexRunAsync(command.Run!, cancellationToken); break;
            case "complete": await store.CompleteIndexRunAsync(command.Run!, command.State!, cancellationToken); break;
            case "stage-replace": await store.StageIndexUnitAsync(command.Replacement!, cancellationToken); break;
            case "stage-delete": await store.StageIndexUnitDeletionAsync(command.RepositoryId!, command.RunId!, command.PluginId!, command.UnitId!, cancellationToken); break;
            default: throw new InvalidDataException($"Unknown persisted Hetu command '{command.Kind}'.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new RepositoryManifestConverter());
        return options;
    }

    internal sealed record PersistedCommand(
        string Kind,
        CodeRepositoryManifest? Repository = null,
        CodeIndexRunManifest? Run = null,
        CodeRepositoryIndexState? State = null,
        CodeIndexUnitReplacement? Replacement = null,
        CodeRepositoryId? RepositoryId = null,
        CodeIndexRunId? RunId = null,
        CodePluginId? PluginId = null,
        CodeIndexUnitId? UnitId = null);

    // Hand-written for durable-log version tolerance: older rows may omit
    // DisplayName/SourceUri, and RegisteredAt defaults to UtcNow. Keep unless
    // System.Text.Json gains an equivalent tolerant path.
    private sealed class RepositoryManifestConverter : JsonConverter<CodeRepositoryManifest>
    {
        public override CodeRepositoryManifest Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return new(
                new CodeRepositoryId(root.GetProperty("Id").GetProperty("Value").GetString()!),
                root.TryGetProperty("DisplayName", out var name) ? name.GetString() : null,
                root.TryGetProperty("SourceUri", out var uri) ? uri.GetString() : null,
                root.GetProperty("RegisteredAt").GetDateTimeOffset());
        }

        public override void Write(
            Utf8JsonWriter writer,
            CodeRepositoryManifest value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("Id");
            writer.WriteString("Value", value.Id.Value);
            writer.WriteEndObject();
            writer.WriteString("DisplayName", value.DisplayName);
            writer.WriteString("SourceUri", value.SourceUri);
            writer.WriteString("RegisteredAt", value.RegisteredAt);
            writer.WriteEndObject();
        }
    }
}
