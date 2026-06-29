using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClipMetaMcp.Protocol;

namespace ClipMetaMcp;

/// <summary>
/// One-command field diagnostic (spec §4): spawns this same executable exactly as an MCP host
/// would, drives the real handshake over real pipes, and prints a pass/fail table. Distills the
/// hard-won MCP debugging checklist ("is it even spawning? is stdout clean?") into something a
/// support thread can ask any user to run. This mode owns stdout, it is human-facing.
/// </summary>
internal static class SelfTest
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    // Request shapes per the MCP spec, built with JsonObject rather than string literals so
    // values are always correctly escaped, and with the protocol version derived from the
    // session's own constant, a future bump cannot leave the selftest silently exercising a
    // stale version.
    private static readonly string InitializeRequest = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = McpSession.LatestProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "clipmetamcp-selftest",
                ["version"] = McpSession.ServerVersion,
            },
        },
    }.ToJsonString();

    private const string InitializedNotification =
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private const string ToolsListRequest =
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private const string PingRequest =
        """{"jsonrpc":"2.0","id":3,"method":"ping"}""";

    private const string LibraryListRequest =
        """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"library_list","arguments":{}}}""";

    /// <summary>
    /// Non-JSON stdout lines seen this run. The per-check failure already names the line; this
    /// feeds the dedicated purity verdict at the end, the single most important property of
    /// the whole server (one stray byte = "Failed to connect").
    /// </summary>
    private static int _nonJsonLines;

    internal static int Run()
    {
        var (exePath, exeArgs) = ResolveServerCommand();
        if (exePath is null)
        {
            Console.WriteLine("FAIL  cannot determine this executable's own path, aborting.");
            return 1;
        }

        Console.WriteLine("clipmetamcp self-test");
        Console.WriteLine($"  exe: {exePath}{(exeArgs.Length > 0 ? " " + string.Join(' ', exeArgs) : "")}");
        Console.WriteLine();

        _nonJsonLines = 0;
        var checks = new List<(string Name, bool Pass, string Detail)>();
        // The child's stderr is its one out-of-band diagnostic channel; drain it continuously
        // (a full, undrained pipe would block the child) and show it on failure.
        var stderr = new StringBuilder();
        // Once a read times out, an orphaned ReadLineAsync still owns the stream, any further
        // read would race it and misattribute responses. Poison the channel instead: every
        // remaining read-dependent check fails fast with an honest "skipped" detail.
        bool channelDead = false;

        // An empty disposable library lets the tools/call round-trip exercise the full sandbox
        // + registry + Core path without touching any real clips.
        string tempLibrary = Path.Combine(Path.GetTempPath(), "clipmetamcp-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLibrary);

        Process? server = null;
        try
        {
            server = Spawn(exePath, exeArgs, stderr, tempLibrary);
            if (server is null)
            {
                checks.Add(("spawn server process", false, "Process.Start returned null"));
                return Report(checks, stderr);
            }
            checks.Add(("spawn server process", true, $"pid {server.Id}"));

            // initialize ─────────────────────────────────────────────────────────────
            Send(server, InitializeRequest);
            JsonObject? init = ReadResponse(server, ref channelDead, out string? initDetail);
            checks.Add(("initialize responds with JSON", init is not null, initDetail ?? "ok"));

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
            JsonObject? toolsList = ReadResponse(server, ref channelDead, out string? toolsDetail);
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
                toolNames.Count > 0 ? string.Join(", ", toolNames) : toolsDetail ?? "no/invalid response"));
            checks.Add(("clip_get_metadata registered", toolNames.Contains("clip_get_metadata"), string.Empty));

            // ping ───────────────────────────────────────────────────────────────────
            Send(server, PingRequest);
            JsonObject? pong = ReadResponse(server, ref channelDead, out string? pingDetail);
            checks.Add(("ping answered", pong is not null && pong["error"] is null, pingDetail ?? string.Empty));

            // tools/call round-trip ──────────────────────────────────────────────────
            // library_list against the empty temp library: proves the dispatch → sandbox →
            // Core path end to end, not just the protocol scaffolding around it.
            Send(server, LibraryListRequest);
            JsonObject? listResult = ReadResponse(server, ref channelDead, out string? listDetail);
            JsonNode? totalMatches = listResult?["result"]?["structuredContent"]?["totalMatches"];
            bool listOk = listResult?["result"]?["isError"] is null &&
                          totalMatches is JsonValue total && total.TryGetValue(out int count) && count == 0;
            checks.Add(("tools/call round-trip (library_list)", listOk,
                listOk ? "empty library listed" : listDetail ?? "unexpected response shape"));

            // clean shutdown ─────────────────────────────────────────────────────────
            server.StandardInput.Close();
            bool exited = server.WaitForExit((int)ExitTimeout.TotalMilliseconds);
            checks.Add(("clean exit when stdin closes", exited && server.ExitCode == 0,
                exited ? $"exit code {server.ExitCode}" : "still running after stdin closed"));

            // stdout purity verdict ──────────────────────────────────────────────────
            // Every line the child wrote was parsed above; any non-JSON line was counted.
            checks.Add(("stdout purity (JSON-RPC only)", _nonJsonLines == 0,
                _nonJsonLines == 0 ? "no stray output" : $"{_nonJsonLines} non-JSON line(s) on stdout"));
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
            try { Directory.Delete(tempLibrary, recursive: true); } catch (IOException) { }
        }

        return Report(checks, stderr);
    }

    /// <summary>
    /// Works out how to re-launch this server. Normally that is <see cref="Environment.ProcessPath"/>
    /// itself; but under `dotnet run -- --selftest` ProcessPath is dotnet.exe, and spawning bare
    /// dotnet would print CLI help to stdout, a false "stray stdout bytes" failure. In that
    /// case the entry assembly is passed as the argument.
    /// </summary>
    private static (string? ExePath, string[] Args) ResolveServerCommand()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null || !File.Exists(exePath))
            return (null, []);

        string hostName = Path.GetFileNameWithoutExtension(exePath);
        if (hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Under the dotnet host, the first command-line arg is the managed dll path.
            // (Not Assembly.Location, that trips IL3000 for the single-file publish, which
            // never takes this branch anyway because its host is the apphost exe.)
            string entryAssembly = Environment.GetCommandLineArgs()[0];
            if (string.IsNullOrEmpty(entryAssembly) || !File.Exists(entryAssembly))
                return (null, []);
            return (exePath, [entryAssembly]);
        }

        return (exePath, []);
    }

    private static Process? Spawn(string exePath, string[] args, StringBuilder stderr, string libraryRoot)
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
        // The child gets the disposable sandbox, not whatever CLIPMETA_LIBRARY_ROOT this
        // process inherited, the round-trip check needs a known-empty library.
        startInfo.Environment[Tools.LibrarySandbox.EnvVarName] = libraryRoot;
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        Process? process = Process.Start(startInfo);
        if (process is not null)
        {
            // Continuous async drain: keeps the child's stderr pipe from filling (which would
            // block the child) and preserves its content for the failure report.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (stderr) stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
        }
        return process;
    }

    private static void Send(Process server, string json)
    {
        // Bare \n framing, matching the MCP stdio transport (never WriteLine: \r\n on Windows).
        server.StandardInput.Write(json);
        server.StandardInput.Write('\n');
        server.StandardInput.Flush();
    }

    /// <summary>
    /// Reads one response line with a timeout and parses it. On timeout the channel is poisoned
    /// (see <c>channelDead</c> at the call site) so later reads cannot race the orphaned one.
    /// <paramref name="detail"/> explains a null return: timeout, dead channel, EOF, or the raw
    /// non-JSON line, the latter being exactly the stray-stdout corruption this diagnostic
    /// exists to catch.
    /// </summary>
    private static JsonObject? ReadResponse(Process server, ref bool channelDead, out string? detail)
    {
        if (channelDead)
        {
            detail = "skipped: stdout channel unusable after an earlier timeout";
            return null;
        }

        Task<string?> read = server.StandardOutput.ReadLineAsync();
        if (!read.Wait(ReadTimeout))
        {
            channelDead = true;
            detail = $"no response within {ReadTimeout.TotalSeconds:0}s";
            return null;
        }

        string? raw = read.Result;
        if (raw is null)
        {
            channelDead = true;
            detail = "server closed stdout (process exited?)";
            return null;
        }

        try
        {
            detail = null;
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            _nonJsonLines++; // feeds the dedicated purity verdict at the end of the run
            detail = $"non-JSON output: \"{(raw.Length <= 60 ? raw : raw[..60] + "…")}\"";
            return null;
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int Report(List<(string Name, bool Pass, string Detail)> checks, StringBuilder stderr)
    {
        foreach ((string name, bool pass, string detail) in checks)
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name,-34} {detail}");

        bool allPassed = checks.All(c => c.Pass);
        Console.WriteLine();

        string capturedStderr;
        lock (stderr) capturedStderr = stderr.ToString();
        if (!allPassed && capturedStderr.Length > 0)
        {
            // The child's own explanation of its failure, show it instead of sending the user
            // off to dig in the log file for information the diagnostic already holds.
            Console.WriteLine("server stderr:");
            foreach (string line in capturedStderr.TrimEnd().Split('\n'))
                Console.WriteLine($"  | {line.TrimEnd('\r')}");
            Console.WriteLine();
        }

        Console.WriteLine(allPassed
            ? $"All {checks.Count} checks passed."
            : @"One or more checks FAILED. Server-side details: %LOCALAPPDATA%\clipmeta\mcp.log");
        return allPassed ? 0 : 1;
    }
}
