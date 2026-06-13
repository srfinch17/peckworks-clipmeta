using System.Text;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaMcp.Protocol;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp;

/// <summary>
/// Entry point. With no arguments, serves MCP over stdio — the mode MCP hosts use.
/// <c>--selftest</c> runs the spawn-and-handshake diagnostic. This file only wires streams and
/// arguments; protocol logic lives in Protocol/, tool plumbing in Tools/, business logic in
/// clipmeta.core (thin-shell rule).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return Serve();

        if (args.Length == 1 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Run();

        Console.Error.WriteLine("Usage: clipmetamcp            serve MCP over stdio (spawned by an MCP host)");
        Console.Error.WriteLine("       clipmetamcp --selftest spawn self and verify the MCP handshake");
        return 2;
    }

    private static int Serve()
    {
        IClipMetaLogger logger = CreateLogger();
        try
        {
            // ── Stdout lockdown (THE IRON RULE, spec §2) ───────────────────────────────
            // The raw stdout stream becomes the protocol channel, owned by exactly one
            // writer. Console.Out is then nulled so any stray Console.WriteLine — current
            // or future code — vanishes instead of corrupting the channel. UTF-8 *without
            // BOM*: a BOM would be three stray bytes ahead of the first response, exactly
            // the corruption this rule exists to prevent.
            var protocolOutput = new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            Console.SetOut(TextWriter.Null);

            using var protocolInput = new StreamReader(
                Console.OpenStandardInput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var sandbox = LibrarySandbox.FromEnvironment();
            var registry = new ToolRegistry();
            ReadTools.RegisterAll(registry, sandbox);
            WriteTools.RegisterAll(registry, sandbox);

            logger.Log(
                $"clipmetamcp {McpSession.ServerVersion} serving; protocol {McpSession.LatestProtocolVersion}; " +
                $"library root: {sandbox.Root ?? "(not configured)"}");

            new McpSession(protocolInput, protocolOutput, registry, logger).Run();

            logger.Log("stdin closed by host; exiting cleanly");
            return 0;
        }
        catch (Exception ex)
        {
            // Fatal startup/loop failure: full details to the log; a one-line summary to
            // stderr, which MCP hosts capture and surface as server logs. Never stdout.
            logger.Log($"FATAL: {ex}");
            Console.Error.WriteLine($"clipmetamcp fatal error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Rotating file logger under <c>%LOCALAPPDATA%\clipmeta\mcp.log</c>, wrapped in
    /// <see cref="SafeLogger"/> so per-line failures (cross-process log contention, AV locks)
    /// can never escape into the session. Falls back to no logging rather than failing
    /// startup — a broken log path must not take the server down.
    /// </summary>
    private static IClipMetaLogger CreateLogger()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "clipmeta", "mcp.log");
            return new SafeLogger(new FileLogger(path));
        }
        catch (Exception)
        {
            return NullLogger.Instance;
        }
    }
}
