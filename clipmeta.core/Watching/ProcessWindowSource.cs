namespace ClipMetaCore.Watching;

/// <summary>Selects the right <see cref="IProcessWindowSource"/> for the running platform.</summary>
public static class ProcessWindowSource
{
    /// <summary>
    /// Returns a Windows process source when running on Windows, otherwise the empty source. The
    /// <see cref="OperatingSystem.IsWindows"/> guard is what makes constructing the Windows source
    /// CA1416-safe.
    /// </summary>
    public static IProcessWindowSource ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsProcessWindowSource() : EmptyProcessWindowSource.Instance;
}
