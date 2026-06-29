using System.Reflection;
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
    // 2025-03-26 is deliberately NOT claimed: that revision requires servers to accept
    // JSON-RPC batch arrays, which this parser rejects (batching was removed in 2025-06-18).
    public static readonly IReadOnlyList<string> SupportedProtocolVersions =
        ["2025-11-25", "2025-06-18"];

    /// <summary>Server name advertised in the initialize result.</summary>
    public const string ServerName = "clipmeta";

    /// <summary>
    /// Server version advertised in the initialize result. Single-sourced from the assembly's
    /// InformationalVersion, which Directory.Build.props stamps from the repo-root VERSION file, so
    /// the exe metadata, the initialize result, and the bundle manifest can never disagree —
    /// pack-mcpb.ps1 stamps the manifest from VERSION and fails the build if it doesn't match the
    /// published exe.
    /// </summary>
    public static readonly string ServerVersion = ReadAssemblyVersion();

    private static string ReadAssemblyVersion()
    {
        string? version = typeof(McpSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
            return "0.0.0"; // unreachable in practice: the csproj always sets InformationalVersion

        // SDK builds may append "+<commit>" source-revision metadata; that suffix is not part of
        // the user-facing version and would never match the manifest.
        int metadataStart = version.IndexOf('+');
        return metadataStart >= 0 ? version[..metadataStart] : version;
    }

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
        // MCP forbids null request ids outright; answer with Invalid Request (null response id,
        // as for unreadable ids) instead of misclassifying the message as a notification.
        if (message.HasExplicitNullId)
        {
            JsonRpcWriter.WriteError(_output, null, JsonRpcErrorCodes.InvalidRequest,
                "Invalid request: 'id' must not be null.");
            return;
        }

        // Notification-vs-request is decided exactly once, here. Notifications never get a
        // response: the only one in our surface (notifications/initialized — the client
        // acknowledging our initialize result) needs no action, and unknown notifications are
        // ignored silently per spec. Everything below this point is a request with a real id.
        if (message.IsNotification)
        {
            if (message.Method != "notifications/initialized")
                _logger.LogVerbose($"ignoring notification '{message.Method ?? "(no method)"}'");
            return;
        }

        JsonNode id = message.Id!; // non-null: notifications returned above, null ids rejected above

        switch (message.Method)
        {
            case "initialize":
                HandleInitialize(id, message.Params);
                break;

            case "tools/list":
                HandleToolsList(id);
                break;

            case "tools/call":
                HandleToolsCall(id, message.Params);
                break;

            case "ping":
                JsonRpcWriter.WriteResult(_output, id, new JsonObject());
                break;

            default:
                JsonRpcWriter.WriteError(_output, id, JsonRpcErrorCodes.MethodNotFound,
                    $"Method not found: {message.Method ?? "(no method)"}");
                break;
        }
    }

    private void HandleInitialize(JsonNode id, JsonNode? requestParams)
    {
        string? requested = null;
        if (requestParams is JsonObject parameters &&
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
        JsonRpcWriter.WriteResult(_output, id, result);
        _logger.LogVerbose($"initialize: client requested '{requested ?? "(none)"}', negotiated '{negotiated}'");
    }

    private void HandleToolsList(JsonNode id)
    {
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
        JsonRpcWriter.WriteResult(_output, id, new JsonObject { ["tools"] = tools });
    }

    private void HandleToolsCall(JsonNode id, JsonNode? requestParams)
    {
        string? name = null;
        JsonObject? arguments = null;
        if (requestParams is JsonObject parameters)
        {
            if (parameters["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? nameString))
                name = nameString;
            arguments = parameters["arguments"] as JsonObject;
        }

        if (name is null)
        {
            JsonRpcWriter.WriteError(_output, id, JsonRpcErrorCodes.InvalidParams,
                "tools/call requires a string 'name' parameter.");
            return;
        }

        if (!_registry.TryGet(name, out ToolDefinition? tool))
        {
            // Per the MCP spec, an unknown tool name is a protocol error (the client offered a
            // tool list; calling outside it is a client bug), unlike tool *execution* failures.
            JsonRpcWriter.WriteError(_output, id, JsonRpcErrorCodes.InvalidParams,
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

        JsonRpcWriter.WriteResult(_output, id, callResult);
    }

    private static JsonObject ToolErrorResult(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };
}
