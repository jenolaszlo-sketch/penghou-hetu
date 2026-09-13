using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hetu;

/// <summary>Shared JSON settings for snapshot transport and hashing.</summary>
internal static class CodeJsonDefaults
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CodeRepositoryManifestConverter());
        return options;
    }
}

/// <summary>
/// Reads and writes repository manifests tolerantly: optional display name,
/// source URI, and registration time fall back to constructor defaults so
/// older payloads keep deserializing. This is the version-tolerance reason
/// the converter exists instead of plain reflection.
/// </summary>
internal sealed class CodeRepositoryManifestConverter : JsonConverter<CodeRepositoryManifest>
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
            root.TryGetProperty("RegisteredAt", out var registered)
                ? registered.GetDateTimeOffset()
                : null);
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
