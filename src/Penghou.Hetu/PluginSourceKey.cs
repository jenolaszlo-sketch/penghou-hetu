namespace Penghou.Hetu;

/// <summary>
/// Identifies one plugin-owned source by value. Replaces newline-joined
/// <c>pluginId + "\n" + sourcePath</c> dictionary keys so no separator
/// invariant is needed.
/// </summary>
internal readonly record struct PluginSourceKey(CodePluginId PluginId, string SourcePath);
