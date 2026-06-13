using System.Text.Json.Nodes;
using ClipMetaMcp.Install;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Tests;

/// <summary>
/// Fixture-config tests for the --install / --uninstall fallback. Everything runs against
/// throwaway files in a temp directory — never the machine's real Claude Desktop config.
/// The contract under test: other people's config survives us byte-meaning-identical,
/// corrupt JSON is refused (never "repaired"), and every change leaves a timestamped backup.
/// </summary>
[TestClass]
public class ClaudeConfigInstallerTests
{
    private string _tempDir = null!;
    private string _configPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "clipmeta-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>A realistic config: another MCP server plus an unrelated top-level setting.</summary>
    private const string ExistingConfig = """
        {
          "preferences": { "theme": "dark" },
          "mcpServers": {
            "esp32matrix": {
              "command": "cmd.exe",
              "args": ["/c", "C:\\mcp\\matrix.cmd"],
              "env": { "BOARD_URL": "http://192.168.1.50" }
            }
          }
        }
        """;

    private InstallResult Install(string? libraryRoot = @"C:\clips") =>
        ClaudeConfigInstaller.InstallInto(
            _configPath, @"C:\tools\clipmetamcp.exe", [], libraryRoot);

    private JsonObject ReadConfig() => (JsonObject)JsonNode.Parse(File.ReadAllText(_configPath))!;

    // ── install ──────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Install_NoExistingFile_CreatesConfigWithEntry()
    {
        InstallResult result = Install();

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNull(result.BackupPath, "nothing existed to back up");
        JsonObject entry = (JsonObject)ReadConfig()["mcpServers"]![ClaudeConfigInstaller.ServerKey]!;
        Assert.AreEqual(@"C:\tools\clipmetamcp.exe", entry["command"]!.GetValue<string>());
        Assert.AreEqual(0, entry["args"]!.AsArray().Count);
        Assert.AreEqual(@"C:\clips",
            entry["env"]![LibrarySandbox.EnvVarName]!.GetValue<string>());
    }

    [TestMethod]
    public void Install_ExistingServers_PreservedExactly()
    {
        File.WriteAllText(_configPath, ExistingConfig);

        InstallResult result = Install();

        Assert.IsTrue(result.Success, result.Message);
        JsonObject config = ReadConfig();
        // The pre-existing server and unrelated settings must survive value-for-value.
        JsonObject other = (JsonObject)config["mcpServers"]!["esp32matrix"]!;
        Assert.AreEqual("cmd.exe", other["command"]!.GetValue<string>());
        Assert.AreEqual(@"C:\mcp\matrix.cmd", other["args"]![1]!.GetValue<string>());
        Assert.AreEqual("http://192.168.1.50", other["env"]!["BOARD_URL"]!.GetValue<string>());
        Assert.AreEqual("dark", config["preferences"]!["theme"]!.GetValue<string>());
        Assert.IsNotNull(config["mcpServers"]![ClaudeConfigInstaller.ServerKey]);
    }

    [TestMethod]
    public void Install_ExistingFile_TimestampedBackupHoldsOriginal()
    {
        File.WriteAllText(_configPath, ExistingConfig);

        InstallResult result = Install();

        Assert.IsNotNull(result.BackupPath);
        StringAssert.Contains(result.BackupPath, ".bak-");
        Assert.AreEqual(ExistingConfig, File.ReadAllText(result.BackupPath),
            "the backup must be the byte-exact pre-install config");
    }

    [TestMethod]
    public void Install_RunTwice_SingleEntryWithLatestSettings()
    {
        Install(@"C:\clips");
        InstallResult second = Install(@"D:\other-clips");

        Assert.IsTrue(second.Success, second.Message);
        JsonObject servers = (JsonObject)ReadConfig()["mcpServers"]!;
        Assert.AreEqual(1, servers.Count, "--install must be idempotent, not duplicative");
        Assert.AreEqual(@"D:\other-clips",
            servers[ClaudeConfigInstaller.ServerKey]!["env"]![LibrarySandbox.EnvVarName]!.GetValue<string>());
    }

    [TestMethod]
    public void Install_NoLibraryRoot_OmitsEnvAndWarnsWritesDisabled()
    {
        InstallResult result = Install(libraryRoot: null);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Message, "WRITE tools stay disabled");
        JsonObject entry = (JsonObject)ReadConfig()["mcpServers"]![ClaudeConfigInstaller.ServerKey]!;
        Assert.IsNull(entry["env"], "no root → no env block, matching server semantics");
    }

    [TestMethod]
    public void Install_CorruptJson_RefusedAndFileUntouched()
    {
        const string corrupt = "{ this is not json";
        File.WriteAllText(_configPath, corrupt);

        InstallResult result = Install();

        Assert.IsFalse(result.Success, "a config we can't parse must be refused, never repaired");
        StringAssert.Contains(result.Message, "Refused");
        Assert.AreEqual(corrupt, File.ReadAllText(_configPath), "original must be untouched");
        Assert.IsNotNull(result.BackupPath);
        Assert.AreEqual(corrupt, File.ReadAllText(result.BackupPath));
    }

    [TestMethod]
    public void Install_McpServersIsNotAnObject_Refused()
    {
        File.WriteAllText(_configPath, """{ "mcpServers": "oops" }""");

        InstallResult result = Install();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("""{ "mcpServers": "oops" }""", File.ReadAllText(_configPath));
    }

    // ── uninstall ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Uninstall_RemovesOnlyTheClipmetaEntry()
    {
        File.WriteAllText(_configPath, ExistingConfig);
        Install();

        InstallResult result = ClaudeConfigInstaller.UninstallFrom(_configPath);

        Assert.IsTrue(result.Success, result.Message);
        JsonObject config = ReadConfig();
        Assert.IsNull(config["mcpServers"]![ClaudeConfigInstaller.ServerKey]);
        Assert.IsNotNull(config["mcpServers"]!["esp32matrix"], "other servers must survive");
        Assert.AreEqual("dark", config["preferences"]!["theme"]!.GetValue<string>());
    }

    [TestMethod]
    public void Uninstall_NoConfigFile_GracefulNoop()
    {
        InstallResult result = ClaudeConfigInstaller.UninstallFrom(_configPath);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Message, "Nothing to do");
        Assert.IsFalse(File.Exists(_configPath), "a no-op must not create the file");
    }

    [TestMethod]
    public void Uninstall_EntryAbsent_GracefulNoop()
    {
        File.WriteAllText(_configPath, ExistingConfig);

        InstallResult result = ClaudeConfigInstaller.UninstallFrom(_configPath);

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Message, "Nothing to do");
        Assert.IsNotNull(ReadConfig()["mcpServers"]!["esp32matrix"]);
    }

    [TestMethod]
    public void Uninstall_CorruptJson_RefusedAndFileUntouched()
    {
        const string corrupt = "[1, 2, ";
        File.WriteAllText(_configPath, corrupt);

        InstallResult result = ClaudeConfigInstaller.UninstallFrom(_configPath);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(corrupt, File.ReadAllText(_configPath));
    }
}
