namespace ClipMetaCore.Watching;

/// <summary>One running process's main-window title, captured at a moment in time.</summary>
public readonly record struct ProcessWindow(string ProcessName, string WindowTitle);
