using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>
/// The single JSON serialization contract for the box tree and box definitions. Both the CLI
/// (<c>clipmetaview --json</c>/<c>--definitions</c>) and the MCP <c>clip_get_boxtree</c> tool
/// route through here, so their output is byte-identical: camelCase keys, string enum names,
/// omitted null properties, compact (no indentation).
/// </summary>
public static class BoxTreeJson
{
    /// <summary>Shared serializer options. Do not mutate; construct a new instance if a variant is ever needed.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>Serializes a box tree to a compact JSON string.</summary>
    public static string ToJson(BoxTree tree) =>
        JsonSerializer.SerializeToNode(tree, Options)!.ToJsonString();

    /// <summary>Serializes a box tree to a <see cref="JsonObject"/> for the MCP handler result.</summary>
    public static JsonObject ToJsonObject(BoxTree tree) =>
        JsonSerializer.SerializeToNode(tree, Options)!.AsObject();

    /// <summary>Serializes the box-definitions dictionary to a compact JSON string.</summary>
    public static string DefinitionsToJson(IReadOnlyDictionary<string, BoxDefinition> defs) =>
        JsonSerializer.SerializeToNode(defs, Options)!.ToJsonString();

    /// <summary>Serializes the box-definitions dictionary to a <see cref="JsonObject"/>.</summary>
    public static JsonObject DefinitionsToJsonObject(IReadOnlyDictionary<string, BoxDefinition> defs) =>
        JsonSerializer.SerializeToNode(defs, Options)!.AsObject();
}
