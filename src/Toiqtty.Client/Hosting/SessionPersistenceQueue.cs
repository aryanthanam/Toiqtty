using Toiqtty.Models;

namespace Toiqtty.Hosting;

/// <summary>Serial persistence of immutable layout snapshots, coalesced without dropping structural saves.</summary>
internal sealed class SessionPersistenceQueue(Func<SavedSession, bool, SessionHostClient?, Task>? writer = null)
{
    private sealed record SaveRequest(SavedSession Snapshot, bool NavigationOnly, SessionHostClient? Host, long Revision);
    private readonly Lock _gate = new();
    private SaveRequest? _pending;
    private TaskCompletionSource? _drained;

    internal Task Enqueue(SavedSession layout, bool navigationOnly, SessionHostClient? host)
    {
        // Capture runtime directories on the caller's UI thread, then detach
        // the entire graph before an await or background file/pipe operation.
        var snapshot = SavedSessionSnapshot.Capture(layout);
        long revision = host?.NextLayoutRevision() ?? 0;
        lock (_gate)
        {
            _pending = new(snapshot, navigationOnly && (_pending?.NavigationOnly ?? true), host, revision);
            if (_drained is null)
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(Run);
            }
            return _drained.Task;
        }
    }

    internal Task FlushAsync() { lock (_gate) return _drained?.Task ?? Task.CompletedTask; }

    private async Task Run()
    {
        Exception? failure = null;
        while (true)
        {
            SaveRequest request;
            lock (_gate)
            {
                if (_pending is null)
                {
                    var drained = _drained!; _drained = null;
                    if (failure is null) drained.TrySetResult(); else drained.TrySetException(failure);
                    return;
                }
                request = _pending; _pending = null;
            }
            try
            {
                if (writer is not null) await writer(request.Snapshot, request.NavigationOnly, request.Host).ConfigureAwait(false);
                else if (request.Host is null) SessionStore.Save(request.Snapshot);
                else if (request.NavigationOnly) await request.Host.SaveNavigationAsync(request.Snapshot).ConfigureAwait(false);
                else await request.Host.SaveAsync(request.Snapshot, request.Revision).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is IOException or OperationCanceledException)
                && request.Host is not null && request.Snapshot.Workspaces.SelectMany(w => w.AllPanes()).All(p => p.IsBrowser))
            { try { SessionStore.Save(request.Snapshot); } catch (Exception persistException) { failure = persistException; } }
            catch (Exception ex) { failure = ex; }
        }
    }
}
