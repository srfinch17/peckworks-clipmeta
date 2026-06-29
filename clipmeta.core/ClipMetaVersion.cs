using System.Reflection;

namespace ClipMetaCore;

/// <summary>
/// The single runtime source of the ClipMeta product version. Every ClipMeta assembly is stamped
/// with the same value from the repo-root <c>VERSION</c> file (via <c>Directory.Build.props</c>),
/// so reading any assembly's <see cref="AssemblyInformationalVersionAttribute"/> yields the product
/// version. The CLIs' <c>--version</c> output reads this; the MCP server advertises the same value
/// in its <c>serverInfo.version</c>. There are NO hardcoded version literals anywhere in the code.
/// </summary>
public static class ClipMetaVersion
{
    /// <summary>The product version (e.g. <c>"1.0.0"</c>), with any SDK <c>+&lt;commit&gt;</c> suffix removed.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        string? version = typeof(ClipMetaVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
            return "0.0.0"; // unreachable in practice: Directory.Build.props always stamps it

        // SDK builds may append "+<commit>" source-revision metadata; that suffix is not part of
        // the user-facing version and would never match the bundle manifest.
        int metadataStart = version.IndexOf('+');
        return metadataStart >= 0 ? version[..metadataStart] : version;
    }
}
