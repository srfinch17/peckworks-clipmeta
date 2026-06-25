using System.Text;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;
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

        if (args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetFlagValue(args, "--library-root", out string? libraryRoot) ||
                !TryGetFlagValue(args, "--config", out string? installConfig))
            {
                return 2;
            }
            return Install.ClaudeConfigInstaller.RunInstall(libraryRoot, installConfig);
        }

        if (args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetFlagValue(args, "--config", out string? uninstallConfig))
                return 2;
            return Install.ClaudeConfigInstaller.RunUninstall(uninstallConfig);
        }

        Console.Error.WriteLine("Usage: clipmetamcp                                     serve MCP over stdio (spawned by an MCP host)");
        Console.Error.WriteLine("       clipmetamcp --selftest                          spawn self and verify the MCP handshake");
        Console.Error.WriteLine("       clipmetamcp --install [--library-root <folder>] add this server to claude_desktop_config.json");
        Console.Error.WriteLine("                             [--config <path>]         (manual fallback when bundle install isn't available)");
        Console.Error.WriteLine("       clipmetamcp --uninstall [--config <path>]       remove this server from the config again");
        return 2;
    }

    /// <summary>
    /// Reads an optional flag's value. Absent flag → true with null value. Flag present but
    /// followed by nothing or by another flag → error message + false, never a silently
    /// swallowed value (the CLI's swallowed-flag lesson; PITFALLS 2026-06-10).
    /// </summary>
    private static bool TryGetFlagValue(string[] args, string flag, out string? value)
    {
        value = null;
        int index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return true;

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Error: {flag} is missing a value. Usage: {flag} <value>");
            return false;
        }
        value = args[index + 1];
        return true;
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

            // Zero-touch flush: a background pump drains the queue as locks clear, so the last clip
            // of a session lands when its player closes without an explicit library_flush_queue.
            // Only meaningful with a configured library; drains run under the same WriteGate as every
            // other write, so the pump can never race a direct write at File.Replace.
            QueueDrainPump? pump = null;
            if (sandbox.Root is { } libraryRoot)
            {
                pump = new QueueDrainPump(
                    libraryRoot, new Mp4Writer(), logger, LockProbe.IsInUse,
                    runExclusive: action =>
                    {
                        WriteGate.Enter();
                        try { action(); }
                        finally { WriteGate.Exit(); }
                    },
                    pollInterval: TimeSpan.FromSeconds(3));
                pump.Start();
            }

            ReadTools.RegisterAll(registry, sandbox);
            WriteTools.RegisterAll(registry, sandbox);
            QueueTools.RegisterAll(registry, sandbox, pump);

            logger.Log(
                $"clipmetamcp {McpSession.ServerVersion} serving; protocol {McpSession.LatestProtocolVersion}; " +
                $"library root: {sandbox.Root ?? "(not configured)"}");

            try
            {
                new McpSession(protocolInput, protocolOutput, registry, logger).Run();
            }
            finally
            {
                pump?.Dispose(); // stop and join the background loop before exit
            }

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
