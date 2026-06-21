using System.Security;

namespace ClipMetaCore.Watching;

/// <summary>
/// Best-effort check of whether a file currently has an open handle that denies exclusive access —
/// the signal that a media player is actively reading it. Cloud-safe: an offline/placeholder file
/// (Dropbox/OneDrive online-only) is reported not-in-use WITHOUT being opened, so the probe can
/// never trigger a hydration download. Never throws — any inaccessible, invalid, missing, or
/// offline path is reported not-in-use; no non-fatal exception ever escapes this method.
/// </summary>
public static class LockProbe
{
    /// <summary>
    /// Returns <see langword="true"/> when the file has an exclusive-denying open handle;
    /// <see langword="false"/> for any inaccessible, invalid, missing, or offline path.
    /// This method never throws for any non-fatal exception.
    /// </summary>
    public static bool IsInUse(string path)
    {
        try
        {
            // Never open an offline/placeholder file — opening would force a download. An
            // un-hydrated file is not the one a player is actively reading, so treat it not-in-use.
            if ((File.GetAttributes(path) & FileAttributes.Offline) != 0)
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // missing/inaccessible/invalid/malformed path — not lockable
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true; // a sharing violation means another handle holds it
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or SecurityException or ArgumentException)
        {
            return false; // inaccessible/invalid/malformed path — not lockable
        }
    }
}
