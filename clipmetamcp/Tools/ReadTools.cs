using System.Text.Json.Nodes;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the read-only tools. Every handler delegates to an already-tested Core operation —
/// the thin-shell rule applies to this MCP server exactly as it does to the CLIs.
/// Phase 1 ships <c>clip_get_metadata</c> only; the remaining read tools arrive in phase 2
/// (see docs/superpowers/plans/2026-06-11-clipmetamcp-server.md).
/// </summary>
public static class ReadTools
{
    /// <summary>Registers all read tools against the given sandbox.</summary>
    public static void RegisterAll(ToolRegistry registry, LibrarySandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "clip_get_metadata",
            "Reads all clipmeta metadata fields stored inside one MP4 game clip. " +
            "'path' must be an existing .mp4 file inside the configured clips library; " +
            "relative paths resolve against the library root. " +
            "Multi-value fields (players, tags, timecode) are returned as pipe-delimited strings.",
            SinglePathSchema(),
            args => GetMetadata(args, sandbox)));
    }

    /// <summary>JSON Schema for tools whose only argument is a clip path.</summary>
    private static JsonObject SinglePathSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to an .mp4 file inside the clips library. " +
                                  "Absolute, or relative to the library root.",
            },
        },
        ["required"] = new JsonArray("path"),
    };

    private static JsonObject GetMetadata(JsonObject? args, LibrarySandbox sandbox)
    {
        string fullPath = sandbox.ResolveClipPath(GetRequiredString(args, "path"));
        BoxNode root = ParseClip(fullPath);

        // The internal schema-version field is write-engine bookkeeping, not user metadata —
        // exclude it here exactly as every CLI read path does.
        var fields = new JsonObject();
        foreach ((string field, string value) in ClipMetaReader.GetFields(root))
        {
            if (!field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal))
                fields[field] = value;
        }

        return new JsonObject
        {
            ["path"] = fullPath,
            ["fields"] = fields,
        };
    }

    /// <summary>Parses an MP4, converting Core's exceptions into model-readable refusals.</summary>
    internal static BoxNode ParseClip(string fullPath)
    {
        try
        {
            return Mp4Parser.ParseFile(fullPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ToolException($"'{fullPath}' could not be parsed as an MP4 file: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolException($"Access to '{fullPath}' was denied by the operating system.");
        }
        catch (IOException ex)
        {
            throw new ToolException($"Could not read '{fullPath}': {ex.Message}");
        }
    }

    /// <summary>Extracts a required string argument or refuses with a message naming it.</summary>
    internal static string GetRequiredString(JsonObject? args, string name)
    {
        if (args?[name] is JsonValue value &&
            value.TryGetValue(out string? text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        throw new ToolException($"The '{name}' argument is required and must be a non-empty string.");
    }
}
