using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Thrown by tool handlers for expected refusals: sandbox violations, missing arguments,
/// unreadable files. The message is written for the model — it states what was wrong and what a
/// valid call looks like, so the model self-corrects instead of retrying blindly. The session
/// converts it to an <c>isError: true</c> tool result; it never carries a stack trace.
/// </summary>
public sealed class ToolException : Exception
{
    /// <summary>Creates a refusal with a model-readable message.</summary>
    public ToolException(string message) : base(message) { }
}

/// <summary>One registered MCP tool: the metadata served by tools/list plus the handler invoked by tools/call.</summary>
/// <param name="Name">Tool name — a snake_case verb phrase the model can reason about.</param>
/// <param name="Description">Description written for the model; states preconditions explicitly.</param>
/// <param name="InputSchema">JSON Schema for the arguments object.</param>
/// <param name="Handler">Maps the arguments object (null when the client sent none) to a structured result.</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject?, JsonObject> Handler);

/// <summary>Name → tool map backing tools/list and tools/call.</summary>
public sealed class ToolRegistry
{
    // List preserves registration order for deterministic tools/list output;
    // the dictionary gives O(1) dispatch.
    private readonly List<ToolDefinition> _ordered = [];
    private readonly Dictionary<string, ToolDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>All registered tools in registration order.</summary>
    public IReadOnlyList<ToolDefinition> All => _ordered;

    /// <summary>Registers a tool. Throws on a duplicate name — that is a programming error, not user input.</summary>
    public void Register(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _byName.Add(tool.Name, tool);
        _ordered.Add(tool);
    }

    /// <summary>Looks up a tool by exact (ordinal) name.</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out ToolDefinition? tool) =>
        _byName.TryGetValue(name, out tool);
}
