using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Watching;
using ClipMetaMcp.Protocol;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Tests.Helpers;

/// <summary>
/// Drives a complete <see cref="McpSession"/> in-process over string streams — no child process,
/// no Claude — and returns the parsed response lines in order.
/// </summary>
internal static class McpHarness
{
    /// <summary>
    /// A standard initialize request. Derives the version from the session's own constant so a
    /// protocol bump cannot leave the tests silently pinned to a stale version.
    /// </summary>
    public static readonly string InitializeRequest = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = McpSession.LatestProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1.0" },
        },
    }.ToJsonString();

    /// <summary>
    /// Feeds <paramref name="requestLines"/> through a fresh session whose tools are sandboxed
    /// to <paramref name="libraryRoot"/> (null = unconfigured) and returns one parsed JsonObject
    /// per response line.
    /// </summary>
    public static IReadOnlyList<JsonObject> Run(string? libraryRoot, params string[] requestLines)
    {
        var registry = new ToolRegistry();
        var sandbox = new LibrarySandbox(libraryRoot);
        ReadTools.RegisterAll(registry, sandbox);
        WriteTools.RegisterAll(registry, sandbox);
        QueueTools.RegisterAll(registry, sandbox);

        using var input = new StringReader(string.Concat(requestLines.Select(line => line + "\n")));
        using var output = new StringWriter();
        new McpSession(input, output, registry, NullLogger.Instance).Run();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();
    }

    /// <summary>
    /// Like <see cref="Run"/>, but wires a shared <paramref name="ledger"/> into both
    /// <see cref="ReadTools"/> and <see cref="WriteTools"/>. Use this when a test needs to
    /// verify that a write marks the ledger and a subsequent watch call honours that mark —
    /// the same instance must back both, mirroring <c>Program.cs</c> production wiring.
    /// </summary>
    public static IReadOnlyList<JsonObject> RunWithLedger(
        string? libraryRoot, SelfActionLedger ledger, params string[] requestLines)
    {
        var registry = new ToolRegistry();
        var sandbox = new LibrarySandbox(libraryRoot);
        ReadTools.RegisterAll(registry, sandbox, watcher: null, ledger: ledger);
        WriteTools.RegisterAll(registry, sandbox, ledger);
        QueueTools.RegisterAll(registry, sandbox);

        using var input = new StringReader(string.Concat(requestLines.Select(line => line + "\n")));
        using var output = new StringWriter();
        new McpSession(input, output, registry, NullLogger.Instance).Run();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();
    }

    /// <summary>
    /// Like <see cref="Run"/>, but wires a shared <paramref name="journal"/> into
    /// <see cref="ReadTools"/> so <c>library_watching</c> surfaces pump auto-flushes as
    /// <c>autoFlushed</c> (report-once). Use this to verify drain-visibility behaviour —
    /// the same journal instance must back both the pump and the tool registration, mirroring
    /// <c>Program.cs</c> production wiring.
    /// </summary>
    public static IReadOnlyList<JsonObject> RunWithJournal(
        string? libraryRoot, DrainJournal journal, params string[] requestLines)
    {
        var registry = new ToolRegistry();
        var sandbox = new LibrarySandbox(libraryRoot);
        ReadTools.RegisterAll(registry, sandbox, watcher: null, ledger: null, journal: journal);
        WriteTools.RegisterAll(registry, sandbox);
        QueueTools.RegisterAll(registry, sandbox);

        using var input = new StringReader(string.Concat(requestLines.Select(line => line + "\n")));
        using var output = new StringWriter();
        new McpSession(input, output, registry, NullLogger.Instance).Run();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();
    }

    /// <summary>Builds a tools/call request line for <paramref name="tool"/>.</summary>
    public static string ToolCall(int id, string tool, JsonObject arguments) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        }.ToJsonString();

    /// <summary>Builds a parameterless request line (tools/list, ping).</summary>
    public static string Request(int id, string method) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method }.ToJsonString();
}
