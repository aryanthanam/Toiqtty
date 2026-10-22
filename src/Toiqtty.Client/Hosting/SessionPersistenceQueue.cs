using Toiqtty.Models;

namespace Toiqtty.Hosting;

/// <summary>Serial persistence of immutable layout snapshots, coalesced without dropping structural saves.</summary>
internal sealed class SessionPersistenceQueue(Func<SavedSession, bool, SessionHostClient?, Task>? writer = null)
{
    private sealed record SaveRequest(SavedSession Snapshot, bool NavigationOnly, Task<SessionHostClient>? Host, TaskCompletionSource Committed);
    private readonly Lock _gate = new();
    private SaveRequest? _pending;
    private TaskCompletionSource? _drained;

    /// <summary>Queue a snapshot of the layout. The task completes once this snapshot, or a
    /// newer one that superseded it while pending, has been written; it faults if that write
    /// failed. The host is awaited and the revision stamped in the serial worker, so a save
    /// can be requested while the host connection is still being made.</summary>
    internal Task Enqueue(SavedSession layout, bool navigationOnly, Task<SessionHostClient>? host)
    {
        // Capture runtime directories on the caller's UI thread, then detach
        // the entire graph before an await or background file/pipe operation.
        var snapshot = SavedSessionSnapshot.Capture(layout);
        lock (_gate)
        {
            _pending = new(snapshot, navigationOnly && (_pending?.NavigationOnly ?? true), host,
                _pending?.Committed ?? new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_drained is null)
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(Run);
            }
            return _pending.Committed.Task;
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
                    // Each request's own task reports its failure; a flush that nobody awaits is not unobserved.
                    if (failure is null) drained.TrySetResult(); else { drained.TrySetException(failure); _ = drained.Task.Exception; }
                    return;
                }
                request = _pending; _pending = null;
            }
            SessionHostClient? host = null;
            try
            {
                // A host that cannot be reached leaves the file as the only owner, as before it was known.
                if (request.Host is not null) try { host = await request.Host.ConfigureAwait(false); } catch (Exception) { host = null; }
                if (writer is not null) await writer(request.Snapshot, request.NavigationOnly, host).ConfigureAwait(false);
                else if (host is null) SessionStore.Save(request.Snapshot);
                else if (request.NavigationOnly) await host.SaveNavigationAsync(request.Snapshot).ConfigureAwait(false);
                else await host.SaveAsync(request.Snapshot).ConfigureAwait(false);
                request.Committed.TrySetResult();
            }
            catch (Exception ex) when ((ex is IOException or OperationCanceledException)
                && host is not null && request.Snapshot.Workspaces.SelectMany(w => w.AllPanes()).All(p => p.IsBrowser))
            {
                try { SessionStore.Save(request.Snapshot); request.Committed.TrySetResult(); }
                catch (Exception persistException) { failure = persistException; request.Committed.TrySetException(persistException); }
            }
            catch (Exception ex) { failure = ex; request.Committed.TrySetException(ex); }
        }
    }
}
