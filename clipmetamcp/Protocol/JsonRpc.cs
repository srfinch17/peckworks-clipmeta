using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipMetaMcp.Protocol;

/// <summary>JSON-RPC 2.0 error codes used by the MCP session.</summary>
public static class JsonRpcErrorCodes
{
    /// <summary>Invalid JSON was received by the server.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON sent is not a valid request object.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>The method does not exist.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Invalid method parameters.</summary>
    public const int InvalidParams = -32602;

    /// <summary>Internal JSON-RPC error.</summary>
    public const int InternalError = -32603;
}

/// <summary>One incoming JSON-RPC 2.0 message: a request (has an id) or a notification (no id).</summary>
public sealed class JsonRpcMessage
{
    /// <summary>
    /// Request id, kept as a raw node and echoed back verbatim in the response. JSON-RPC allows
    /// number or string ids; storing the node avoids ever re-typing it. Null for notifications.
    /// </summary>
    public JsonNode? Id { get; private init; }

    /// <summary>Method name; null when the message carried no string method.</summary>
    public string? Method { get; private init; }

    /// <summary>Raw params node; null when absent.</summary>
    public JsonNode? Params { get; private init; }

    /// <summary>
    /// True when the message carried an explicit <c>"id": null</c>. MCP forbids null ids
    /// ("Unlike base JSON-RPC, the ID MUST NOT be null"), but a JSON-null id parses to the same
    /// .NET null as an absent id, without this flag such a request would be misclassified as a
    /// notification and silently never answered.
    /// </summary>
    public bool HasExplicitNullId { get; private init; }

    /// <summary>True when no id was present, notifications never get a response.</summary>
    public bool IsNotification => Id is null;

    private JsonRpcMessage() { }

    /// <summary>
    /// Parses one line of JSON into a message. Throws <see cref="JsonException"/> on malformed
    /// JSON or a non-object payload; the caller maps that to a -32700 parse error.
    /// </summary>
    public static JsonRpcMessage Parse(string line)
    {
        JsonNode? node = JsonNode.Parse(line);
        if (node is not JsonObject obj)
            throw new JsonException("A JSON-RPC message must be a JSON object.");

        string? method = null;
        if (obj.TryGetPropertyValue("method", out JsonNode? methodNode) &&
            methodNode is JsonValue methodValue && methodValue.TryGetValue(out string? methodString))
        {
            method = methodString;
        }

        bool hasIdProperty = obj.TryGetPropertyValue("id", out JsonNode? idNode);
        obj.TryGetPropertyValue("params", out JsonNode? paramsNode);

        // DeepClone detaches the nodes from the parsed message so they can be re-parented
        // into a response object later (a JsonNode may only ever have one parent).
        return new JsonRpcMessage
        {
            Id = idNode?.DeepClone(),
            Method = method,
            Params = paramsNode?.DeepClone(),
            HasExplicitNullId = hasIdProperty && idNode is null,
        };
    }
}

/// <summary>
/// Writes JSON-RPC 2.0 responses as single newline-terminated lines of compact JSON, the MCP
/// stdio framing. Compact serialization guarantees no embedded raw newlines (newlines inside
/// string values are escaped as \n by the serializer).
/// </summary>
public static class JsonRpcWriter
{
    /// <summary>Writes a success response carrying <paramref name="result"/>.</summary>
    public static void WriteResult(TextWriter output, JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };
        WriteLine(output, response);
    }

    /// <summary>
    /// Writes an error response. A null <paramref name="id"/> is serialized as JSON null,
    /// which JSON-RPC requires for requests whose id could not be read (parse errors).
    /// </summary>
    public static void WriteError(TextWriter output, JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        WriteLine(output, response);
    }

    private static void WriteLine(TextWriter output, JsonObject response)
    {
        output.Write(response.ToJsonString());
        // Bare \n framing per the MCP stdio transport, never WriteLine, whose newline is
        // platform-dependent (\r\n on Windows).
        output.Write('\n');
        output.Flush();
    }
}
