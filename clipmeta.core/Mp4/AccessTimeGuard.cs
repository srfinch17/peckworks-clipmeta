namespace ClipMetaCore.Mp4;

/// <summary>
/// Captures a file's <see cref="File.GetLastAccessTimeUtc(string)"/> on construction and restores
/// it on <see cref="Dispose"/>, best-effort. ClipMeta's own reads would otherwise bump the access
/// time and pollute the watched-clip access-time signal. Restoring is itself a metadata write that
/// can fail (file locked by a player, read-only, removed); such failures are swallowed, preserving
/// the signal must never break a read.
/// </summary>
public readonly struct AccessTimeGuard : IDisposable
{
    private readonly string _path;
    private readonly DateTime _original;
    private readonly bool _captured;

    /// <summary>Captures the current last-access time of <paramref name="path"/>, best-effort.</summary>
    public AccessTimeGuard(string path)
    {
        _path = path;
        try
        {
            _original = File.GetLastAccessTimeUtc(path);
            _captured = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _original = default;
            _captured = false;
        }
    }

    /// <summary>Restores the captured last-access time, best-effort (failures swallowed).</summary>
    public void Dispose()
    {
        if (!_captured)
            return;
        try
        {
            File.SetLastAccessTimeUtc(_path, _original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // best-effort: restoring is a write that can lose to a lock or a vanished file.
        }
    }
}
