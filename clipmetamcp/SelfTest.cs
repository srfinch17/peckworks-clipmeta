using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClipMetaMcp.Protocol;

namespace ClipMetaMcp;

/// <summary>
/// One-command field diagnostic (spec §4): spawns this same executable exactly as an MCP host
/// would, drives the real handshake over real pipes, and prints a pass/fail table. Distills the
/// hard-won MCP debugging checklist ("is it even spawning? is stdout clean?") into something a
/// support thread can ask any user to run. This mode owns stdout — it is human-facing.
/// </summary>
internal static class SelfTest
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"clipmetamcp-selftest","version":"1.0.0"}}}""";

    private const string InitializedNotification =
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private const string ToolsListRequest =
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private const string PingRequest =
        """{"jsonrpc":"2.0","id":3,"method":"ping"}""";

    internal static int Run()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null || !File.Exists(exePath))
        {
            Console.WriteLine("FAIL  cannot determine this executable's own path — aborting.");
            return 1;
        }

        Console.WriteLine("clipmetamcp self-test");
        Console.WriteLine($"  exe: {exePath}");
        Console.WriteLine();

        var checks = new List<(string Name, bool Pass, string Detail)>();
        Process? server = null;
        try
        {
            server = Spawn(exePath);
            if (server is null)
            {
                checks.Add(("spawn server process", false, "Process.Start returned null"));
                return Report(checks);
            }
            checks.Add(("spawn server process", true, $"pid {server.Id}"));

            // initialize ─────────────────────────────────────────────────────────────
            Send(server, InitializeRequest);
            JsonObject? init = ReadResponse(server, out string? initRaw);
            checks.Add(("initialize responds with JSON", init is not null,
                init is not null ? "ok" : Truncate(initRaw) ?? $"no response within {ReadTimeout.TotalSeconds:0}s"));

            if (init is not null)
            {
                string? version = Str(init["result"]?["protocolVersion"]);
                checks.Add(("protocol version negotiated", version is not null, version ?? "missing"));
                checks.Add(("tools capability advertised",
                    init["result"]?["capabilities"]?["tools"] is JsonObject, string.Empty));
                string? serverName = Str(init["result"]?["serverInfo"]?["name"]);
                checks.Add(("server identifies as clipmeta",
                    serverName == McpSession.ServerName, serverName ?? "missing"));
            }

            Send(server, InitializedNotification);

            // tools/list ─────────────────────────────────────────────────────────────
            Send(server, ToolsListRequest);
            JsonObject? toolsList = ReadResponse(server, out string? toolsRaw);
            var toolNames = new List<string>();
            if (toolsList?["result"]?["tools"] is JsonArray toolsArray)
            {
                foreach (JsonNode? tool in toolsArray)
                {
                    if (Str(tool?["name"]) is string toolName)
                        toolNames.Add(toolName);
                }
            }
            checks.Add(("tools/list returns tools", toolNames.Count > 0,
                toolNames.Count > 0 ? string.Join(", ", toolNames) : Truncate(toolsRaw) ?? "no/invalid response"));
            checks.Add(("clip_get_metadata registered", toolNames.Contains("clip_get_metadata"), string.Empty));

            // ping ───────────────────────────────────────────────────────────────────
            Send(server, PingRequest);
            JsonObject? pong = ReadResponse(server, out _);
            checks.Add(("ping answered", pong is not null && pong["error"] is null, string.Empty));

            // clean shutdown ─────────────────────────────────────────────────────────
            server.StandardInput.Close();
            bool exited = server.WaitForExit((int)ExitTimeout.TotalMilliseconds);
            checks.Add(("clean exit when stdin closes", exited && server.ExitCode == 0,
                exited ? $"exit code {server.ExitCode}" : "still running after stdin closed"));
        }
        catch (Exception ex)
        {
            checks.Add(("self-test crashed", false, ex.Message));
        }
        finally
        {
            if (server is not null && !server.HasExited)
                server.Kill(entireProcessTree: true);
            server?.Dispose();
        }

        return Report(checks);
    }

    private static Process? Spawn(string exePath)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        return Process.Start(startInfo);
    }

    private static void Send(Process server, string json)
    {
        // Bare \n framing, matching the MCP stdio transport (never WriteLine: \r\n on Windows).
        server.StandardInput.Write(json);
        server.StandardInput.Write('\n');
        server.StandardInput.Flush();
    }

    /// <summary>
    /// Reads one response line with a timeout and parses it. <paramref name="raw"/> carries the
    /// raw line when it arrived but failed to parse — that is exactly the "stray stdout bytes"
    /// failure this diagnostic exists to catch, so it is surfaced in the report.
    /// </summary>
    private static JsonObject? ReadResponse(Process server, out string? raw)
    {
        Task<string?> read = server.StandardOutput.ReadLineAsync();
        raw = read.Wait(ReadTimeout) ? read.Result : null;
        if (raw is null)
            return null;
        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string? Truncate(string? raw) =>
        raw is null ? null : $"non-JSON output: \"{(raw.Length <= 60 ? raw : raw[..60] + "…")}\"";

    private static int Report(List<(string Name, bool Pass, string Detail)> checks)
    {
        foreach ((string name, bool pass, string detail) in checks)
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name,-34} {detail}");

        bool allPassed = checks.All(c => c.Pass);
        Console.WriteLine();
        Console.WriteLine(allPassed
            ? $"All {checks.Count} checks passed."
            : @"One or more checks FAILED. Server-side details: %LOCALAPPDATA%\clipmeta\mcp.log");
        return allPassed ? 0 : 1;
    }
}
