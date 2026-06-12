using System.Text.Json;
using System.Text.Json.Nodes;
using ClipMetaCore.Abstractions;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Protocol;

/// <summary>
/// One MCP session over a pair of text streams: reads newline-delimited JSON-RPC requests,
/// dispatches them, and writes newline-delimited responses. Deliberately transport-agnostic —
/// production binds it to stdin/stdout, tests drive it with StringReader/StringWriter.
/// </summary>
public sealed class McpSession
{
    /// <summary>
    /// Newest MCP protocol revision this server implements. Verified current at
    /// modelcontextprotocol.io on 2026-06-11 (spec risk R3: re-check on upgrade).
    /// </summary>
    public const string LatestProtocolVersion = "2025-11-25";

    /// <summary>
    /// All revisions we can honestly claim: our surface (lifecycle + tools) is identical across
    /// these. Negotiation rule: if the client requests one of these we echo it back; anything
    /// else gets <see cref="LatestProtocolVersion"/> and the client decides whether to proceed.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26"];

    /// <summary>Server name advertised in the initialize result.</summary>
    public const string ServerName = "clipmeta";

    /// <summary>Server version advertised in the initialize result.</summary>
    public const string ServerVersion = "1.0.0";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ToolRegistry _registry;
    private readonly IClipMetaLogger _logger;

    /// <summary>Creates a session bound to the given streams, tool registry, and logger.</summary>
    public McpSession(TextReader input, TextWriter output, ToolRegistry registry, IClipMetaLogger logger)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _input = input;
        _output = output;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Runs the dispatch loop until the input stream ends (the host closed stdin).</summary>
    public void Run()
    {
        string? line;
        while ((line = _input.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonRpcMessage message;
            try
            {
                message = JsonRpcMessage.Parse(line);
            }
            catch (JsonException ex)
            {
                // Malformed input must never kill the session — answer and keep reading.
                _logger.Log($"parse error: {ex.Message}");
                JsonRpcWriter.WriteError(_output, null, JsonRpcErrorCodes.ParseError, "Parse error: invalid JSON.");
                continue;
            }

            try
            {
                Dispatch(message);
            }
            catch (Exception ex)
            {
                // Last-ditch guard: a dispatch bug must not kill the session or leak a stack
                // trace onto the protocol channel. Full details go to the log file only.
                _logger.Log($"internal error dispatching '{message.Method}': {ex}");
                if (!message.IsNotification)
                    JsonRpcWriter.WriteError(_output, message.Id, JsonRpcErrorCodes.InternalError, "Internal error.");
            }
        }
    }

    private void Dispatch(JsonRpcMessage message)
    {
        switch (message.Method)
        {
            case "initialize":
                HandleInitialize(message);
                break;

            case "notifications/initialized":
                // Lifecycle notification: the client acknowledged our initialize result.
                // Nothing to do, and notifications never get a response.
                break;

            case "tools/list":
                HandleToolsList(message);
                break;

            case "tools/call":
                HandleToolsCall(message);
                break;

            case "ping":
                if (!message.IsNotification)
                    JsonRpcWriter.WriteResult(_output, message.Id!, new JsonObject());
                break;

            default:
                // Unknown notifications are ignored silently per spec; unknown requests get -32601.
                if (!message.IsNotification)
                    JsonRpcWriter.WriteError(_output, message.Id, JsonRpcErrorCodes.MethodNotFound,
                        $"Method not found: {message.Method ?? "(no method)"}");
                break;
        }
    }

    private void HandleInitialize(JsonRpcMessage message)
    {
        if (message.IsNotification)
            return; // initialize must be a request; a malformed notification form is ignored

        string? requested = null;
        if (message.Params is JsonObject parameters &&
            parameters["protocolVersion"] is JsonValue versionValue &&
            versionValue.TryGetValue(out string? versionString))
        {
            requested = versionString;
        }

        string negotiated = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : LatestProtocolVersion;

        var result = new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
        };
        JsonRpcWriter.WriteResult(_output, message.Id!, result);
        _logger.LogVerbose($"initialize: client requested '{requested ?? "(none)"}', negotiated '{negotiated}'");
    }

    private void HandleToolsList(JsonRpcMessage message)
    {
        if (message.IsNotification)
            return;

        var tools = new JsonArray();
        foreach (ToolDefinition tool in _registry.All)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                // The registry owns the schema instance; clone it so the list can be served
                // repeatedly (a JsonNode may only have one parent).
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        JsonRpcWriter.WriteResult(_output, message.Id!, new JsonObject { ["tools"] = tools });
    }

    private void HandleToolsCall(JsonRpcMessage message)
    {
        if (message.IsNotification)
            return;

        string? name = null;
        JsonObject? arguments = null;
        if (message.Params is JsonObject parameters)
        {
            if (parameters["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? nameString))
                name = nameString;
            arguments = parameters["arguments"] as JsonObject;
        }

        if (name is null)
        {
            JsonRpcWriter.WriteError(_output, message.Id, JsonRpcErrorCodes.InvalidParams,
                "tools/call requires a string 'name' parameter.");
            return;
        }

        if (!_registry.TryGet(name, out ToolDefinition? tool))
        {
            // Per the MCP spec, an unknown tool name is a protocol error (the client offered a
            // tool list; calling outside it is a client bug), unlike tool *execution* failures.
            JsonRpcWriter.WriteError(_output, message.Id, JsonRpcErrorCodes.InvalidParams,
                $"Unknown tool: {name}");
            return;
        }

        JsonObject callResult;
        try
        {
            JsonObject structured = tool.Handler(arguments);
            callResult = new JsonObject
            {
                // Text block for universal client compatibility; structuredContent so the model
                // reasons over real JSON instead of re-parsing prose (spec §3).
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structured.ToJsonString(),
                }),
                ["structuredContent"] = structured,
            };
            _logger.Log($"tool {name}: ok");
        }
        catch (ToolException ex)
        {
            // Expected refusal (sandbox, bad argument, unreadable file). The message is written
            // for the model so it can self-correct. Tool errors, never protocol errors (spec §2).
            callResult = ToolErrorResult(ex.Message);
            _logger.Log($"tool {name}: refused — {ex.Message}");
        }
        catch (Exception ex)
        {
            // Unexpected failure: human-readable summary to the model, full stack to the log only.
            callResult = ToolErrorResult($"The {name} tool failed: {ex.Message}");
            _logger.Log($"tool {name}: failed — {ex}");
        }

        JsonRpcWriter.WriteResult(_output, message.Id!, callResult);
    }

    private static JsonObject ToolErrorResult(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };
}
