using Oroterm.Models;
using Oroterm.Terminal;

namespace Oroterm.Hosting;

/// <summary>Owns completion of explicitly requested shell closures; layout edits own no processes.</summary>
internal sealed class SessionLifetimeCoordinator
{
    private readonly List<Task> _closures = [];

    internal void ClosePanes(IEnumerable<PaneNode> panes)
    {
        _closures.RemoveAll(task => task.IsCompletedSuccessfully);
        foreach (var session in panes.Select(p => p.Session).OfType<ITerminalSession>().Distinct())
            _closures.Add(session.CloseAsync().AsTask());
    }

    internal async Task DrainAsync()
    {
        var pending = _closures.ToArray(); _closures.Clear();
        await Task.WhenAll(pending);
    }

    internal static Task DetachAsync(IEnumerable<PaneNode> panes)
        => Task.WhenAll(panes.Select(p => p.Session).OfType<ITerminalSession>().Distinct()
            .Select(session => session.DetachAsync().AsTask()));
}
