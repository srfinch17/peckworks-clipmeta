using System.Text.Json.Nodes;
using ClipMetaMcp.Protocol;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>Protocol-shape tests: handshake, negotiation, dispatch, and error behavior.</summary>
[TestClass]
public class McpSessionTests
{
    private static string InitializeWithVersion(string version) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1.0" },
            },
        }.ToJsonString();

    [TestMethod]
    public void Initialize_LatestVersion_EchoesIt()
    {
        var responses = McpHarness.Run(null, McpHarness.InitializeRequest);

        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual("2025-11-25", responses[0]["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [TestMethod]
    public void Initialize_OlderSupportedVersion_EchoesIt()
    {
        var responses = McpHarness.Run(null, InitializeWithVersion("2025-06-18"));

        Assert.AreEqual("2025-06-18", responses[0]["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [TestMethod]
    public void Initialize_UnknownVersion_RespondsWithLatest()
    {
        var responses = McpHarness.Run(null, InitializeWithVersion("9999-01-01"));

        Assert.AreEqual(McpSession.LatestProtocolVersion,
            responses[0]["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [TestMethod]
    public void Initialize_AdvertisesToolsCapability()
    {
        var responses = McpHarness.Run(null, McpHarness.InitializeRequest);

        Assert.IsInstanceOfType<JsonObject>(responses[0]["result"]?["capabilities"]?["tools"]);
    }

    [TestMethod]
    public void Initialize_IdentifiesServer()
    {
        var responses = McpHarness.Run(null, McpHarness.InitializeRequest);

        Assert.AreEqual(McpSession.ServerName,
            responses[0]["result"]?["serverInfo"]?["name"]?.GetValue<string>());
    }

    [TestMethod]
    public void Initialize_EchoesRequestId()
    {
        var responses = McpHarness.Run(null, McpHarness.InitializeRequest);

        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public void ToolsList_ContainsClipGetMetadata_WithInputSchema()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            McpHarness.Request(2, "tools/list"));

        var tools = (JsonArray)responses[1]["result"]!["tools"]!;
        JsonNode? tool = tools.SingleOrDefault(t => t?["name"]?.GetValue<string>() == "clip_get_metadata");

        Assert.IsNotNull(tool, "clip_get_metadata not present in tools/list");
        Assert.IsFalse(string.IsNullOrWhiteSpace(tool["description"]?.GetValue<string>()));
        Assert.AreEqual("object", tool["inputSchema"]?["type"]?.GetValue<string>());
        var required = (JsonArray)tool["inputSchema"]!["required"]!;
        Assert.IsTrue(required.Any(r => r?.GetValue<string>() == "path"));
    }

    [TestMethod]
    public void ToolsList_ServedTwice_ReturnsSameTools()
    {
        // Guards the schema-DeepClone behavior: serving the registry twice must not throw
        // (a JsonNode can only have one parent) and must return identical content.
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            McpHarness.Request(2, "tools/list"),
            McpHarness.Request(3, "tools/list"));

        Assert.AreEqual(
            responses[1]["result"]!["tools"]!.ToJsonString(),
            responses[2]["result"]!["tools"]!.ToJsonString());
    }

    [TestMethod]
    public void Ping_ReturnsEmptyResult()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            McpHarness.Request(2, "ping"));

        Assert.AreEqual(2, responses[1]["id"]?.GetValue<int>());
        Assert.IsInstanceOfType<JsonObject>(responses[1]["result"]);
        Assert.IsNull(responses[1]["error"]);
    }

    [TestMethod]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            McpHarness.Request(2, "resources/list"));

        Assert.AreEqual(JsonRpcErrorCodes.MethodNotFound,
            responses[1]["error"]?["code"]?.GetValue<int>());
    }

    [TestMethod]
    public void UnknownNotification_IsIgnoredSilently()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            """{"jsonrpc":"2.0","method":"notifications/whatever"}""",
            McpHarness.Request(2, "ping"));

        // Only the initialize and ping responses — nothing for the notification.
        Assert.AreEqual(2, responses.Count);
        Assert.AreEqual(2, responses[1]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public void MalformedJson_ReturnsParseError_AndSessionSurvives()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            "{this is not json",
            McpHarness.Request(2, "ping"));

        Assert.AreEqual(3, responses.Count);
        Assert.AreEqual(JsonRpcErrorCodes.ParseError, responses[1]["error"]?["code"]?.GetValue<int>());
        // JSON-RPC requires id null when the request id could not be read.
        Assert.IsNull(responses[1]["id"], "parse-error response must carry id null");
        // The session kept going: the ping after the garbage was answered.
        Assert.AreEqual(2, responses[2]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public void ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "no_such_tool", new JsonObject()));

        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams,
            responses[1]["error"]?["code"]?.GetValue<int>());
    }

    [TestMethod]
    public void ToolsCall_MissingName_ReturnsInvalidParams()
    {
        var responses = McpHarness.Run(null,
            McpHarness.InitializeRequest,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}""");

        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams,
            responses[1]["error"]?["code"]?.GetValue<int>());
    }

    [TestMethod]
    public void ExplicitNullId_ReturnsInvalidRequest()
    {
        // MCP forbids null ids; without explicit handling this parses identically to an absent
        // id and the request would be silently dropped as a notification.
        var responses = McpHarness.Run(null,
            """{"jsonrpc":"2.0","id":null,"method":"ping"}""");

        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(JsonRpcErrorCodes.InvalidRequest, responses[0]["error"]?["code"]?.GetValue<int>());
        Assert.IsNull(responses[0]["id"]);
    }

    [TestMethod]
    public void Initialize_BatchOnlyVersion20250326_IsNotClaimed()
    {
        // 2025-03-26 mandates batch-receive support this parser does not have; claiming it
        // would be a lie. A client requesting it gets our latest instead.
        var responses = McpHarness.Run(null, InitializeWithVersion("2025-03-26"));

        Assert.AreEqual(McpSession.LatestProtocolVersion,
            responses[0]["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [TestMethod]
    public void StringRequestId_IsEchoedAsString()
    {
        var responses = McpHarness.Run(null,
            """{"jsonrpc":"2.0","id":"abc-123","method":"ping"}""");

        Assert.AreEqual("abc-123", responses[0]["id"]?.GetValue<string>());
    }
}
