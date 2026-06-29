namespace ClipMetaCore.Watching;

/// <summary>
/// A source that reports no players, the default on non-Windows platforms and anywhere a real
/// source is not wired. Resolution then relies on the access-time signal alone.
/// </summary>
public sealed class EmptyProcessWindowSource : IProcessWindowSource
{
    /// <summary>The shared instance.</summary>
    public static EmptyProcessWindowSource Instance { get; } = new();

    private EmptyProcessWindowSource() { }

    /// <inheritdoc/>
    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames) =>
        Array.Empty<ProcessWindow>();
}
