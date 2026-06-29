using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>Returns a fixed set of player windows, ignoring the name filter, for resolver tests.</summary>
internal sealed class FakeProcessWindowSource : IProcessWindowSource
{
    private readonly IReadOnlyList<ProcessWindow> _windows;

    public FakeProcessWindowSource(params ProcessWindow[] windows) => _windows = windows;

    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames) => _windows;
}
