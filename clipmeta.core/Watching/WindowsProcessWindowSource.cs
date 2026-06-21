using System.Diagnostics;
using System.Runtime.Versioning;

namespace ClipMetaCore.Watching;

/// <summary>
/// Reads media-player window titles via <see cref="Process.MainWindowTitle"/> (Windows-only).
/// Title reads are wrapped per-process: a process can exit between enumeration and access, or deny
/// access to its window, and that must skip the process, never fail the whole snapshot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessWindowSource : IProcessWindowSource
{
    /// <inheritdoc/>
    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames)
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<ProcessWindow>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(process.ProcessName))
                    continue;
                string title = process.MainWindowTitle;
                if (!string.IsNullOrEmpty(title))
                    results.Add(new ProcessWindow(process.ProcessName, title));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process exited mid-enumeration, or its window is inaccessible — skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }
}
