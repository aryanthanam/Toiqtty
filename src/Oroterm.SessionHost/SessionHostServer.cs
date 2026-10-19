using System.IO.Pipes;
using Oroterm.Models;
using Oroterm.Terminal;

namespace Oroterm.Hosting;

/// <summary>The only owner of persistent ConPTY handles and libghostty instances.</summary>
public sealed partial class SessionHostServer : IAsyncDisposable
{
    private sealed class Entry(TerminalSession session)
    {
        public readonly TerminalSession Session=session;
        public readonly object CaptureGate=new();
        public TerminalCell[] Cells=TerminalCellPool.Shared.Rent(3000);
        public bool Closed;
        public ushort Columns=100, Rows=30;
        public long AnimationDue=long.MaxValue;
        public long Attention;
        public int PendingAttention;
        public int CapturedVersion=-1;
        public long FrameRevision, LastCapture;
        public uint CapturedColumns,CapturedRows;
        public bool RenderHeld;
        public long OutputRevision;
        public TaskCompletionSource? OutputPulse;
    }
    private readonly object _gate=new();
    private readonly Dictionary<string,Entry> _sessions=[];
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _connections=new(32);
    private readonly List<Task> _requests=[];
    private readonly string _folder, _identity=Guid.NewGuid().ToString("N");
    private SavedSession _layout;
    private bool _created;
    private bool _shutdownPending;
    private int _shutdownReplies;
    private long _emptySince=Environment.TickCount64;
    private long _outputRevision;
    private long _transportAcceptRetries;
    private readonly object _outputGate=new();
    private TaskCompletionSource? _outputPulse;
    private void OutputChanged(Entry entry)
    {
        lock(_outputGate)
        {
            entry.OutputRevision=++_outputRevision;
            entry.OutputPulse?.TrySetResult(); entry.OutputPulse=null;
            _outputPulse?.TrySetResult(); _outputPulse=null;
        }
    }
    public SessionHostServer(string folder) { _folder=folder; _layout=SessionStore.Load(); }
    public async Task Run()
    {
        Directory.CreateDirectory(_folder);
        await using var lease=new FileStream(Path.Combine(_folder,"host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        File.WriteAllText(Path.Combine(_folder,"host.json"),System.Text.Json.JsonSerializer.Serialize(new { processId=Environment.ProcessId, host=_identity, protocol=HostProtocol.Version }));
        Task monitor=MonitorLifetime();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _connections.WaitAsync(_stop.Token);
                var pipe=new NamedPipeServerStream(HostProtocol.PipeName(_folder),PipeDirection.InOut,32,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch(IOException ex) when((ex.HResult&0xffff) is 109 or 232 or 233)
                {
                    // A client can close between ConnectNamedPipe creation and
                    // acceptance. That connection owns no session or request.
                    pipe.Dispose(); _connections.Release(); Interlocked.Increment(ref _transportAcceptRetries); continue;
                }
                catch { pipe.Dispose(); _connections.Release(); throw; }
                // Async handlers can run synchronously through ready I/O into
                // native resize/capture work. Do not stall new pipe acceptance
                // behind that work; the existing connection limit still bounds it.
                Task request=Task.Run(()=>Handle(pipe));
                lock (_requests) { _requests.RemoveAll(t=>t.IsCompleted); _requests.Add(request); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel(); await monitor;
            Task[] pending; lock (_requests) pending=_requests.ToArray();
            await Task.WhenAll(pending);
        }
    }
    private async Task Handle(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            bool shutdownReply = false;
            try
            {
                var request=await HostProtocol.ReadJson<HostRequest>(pipe,deadline.Token);
                HostReply reply;
                try
                {
                    if (request.Protocol!=HostProtocol.Version) throw new IOException("Incompatible session-host protocol. Stop existing sessions before upgrading the host.");
                    if (request.Numbers is null || request.Images is null || request.Pane is null || request.Text is null || request.Directory is null) throw new ArgumentException("Invalid session request.");
                    if (request.Operation!="ping" && request.Host!=_identity) throw new IOException("The session host changed. Reconnect explicitly; input was not sent.");
                    if (request.Operation == "shutdown")
                    {
                        lock (_gate) { _shutdownPending = true; Interlocked.Increment(ref _shutdownReplies); }
                        shutdownReply = true;
                    }
                    if(request.Operation=="watch" && request.WatchPanes is not null)
                    {
                        using var watchStop=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        var disconnected=ObserveWatchDisconnect(pipe,watchStop);
                        try { reply=await Dispatch(request,watchStop.Token); }
                        finally { watchStop.Cancel(); await disconnected; }
                    }
                    else reply=await Dispatch(request,_stop.Token);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { reply=new() { Error=ex.Message }; }
                reply.Host=_identity; reply.ProcessId=Environment.ProcessId; reply.BinaryLength=reply.Binary.Length;
                try { await HostProtocol.WriteJson(pipe,reply,deadline.Token); }
                catch(HostMessageTooLargeException) when(request.Operation=="states")
                {
                    // WriteJson checks its limit before sending any header.
                    // Report a small, explicit error so a read-only client can
                    // split the batch without losing unrelated pane metadata.
                    reply=new() { Host=_identity,ProcessId=Environment.ProcessId,Error=HostProtocol.MetadataBatchTooLarge };
                    await HostProtocol.WriteJson(pipe,reply,deadline.Token);
                }
                if (reply.Binary.Length>0) await pipe.WriteAsync(reply.Binary,deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                // Acknowledged shutdowns must finish writing before cancellation
                // tears down their pipe. Failed/disconnected requesters still stop
                // the host after the already-authorized shutdown is processed.
                if (shutdownReply && Interlocked.Decrement(ref _shutdownReplies) == 0) _stop.Cancel();
                _connections.Release();
            }
        }
    }
    private static async Task ObserveWatchDisconnect(NamedPipeServerStream pipe,CancellationTokenSource watchStop)
    {
        try { await pipe.ReadAsync(new byte[1],watchStop.Token).ConfigureAwait(false); watchStop.Cancel(); }
        catch(OperationCanceledException) when(watchStop.IsCancellationRequested) { }
        catch(IOException) { watchStop.Cancel(); }
    }
    private ShellState State(Entry entry)
    {
        var s=entry.Session;
        return new(s.Version,s.HasExited,s.ExitCode,s.Title,s.StatusSummary,s.Directory,s.WorkingDirectory,s.LocalDirectory,s.Performance,s.Status,Interlocked.Read(ref entry.Attention),Volatile.Read(ref entry.PendingAttention)!=0) { MouseTracking=s.MouseTracking };
    }
    private async Task<HostReply> Dispatch(HostRequest r,CancellationToken watchCancellation)
    {
        if (r.Operation=="ping")
        {
            lock (_gate) return new() { Layout=SavedSessionSnapshot.Capture(_layout), Panes=_sessions.Keys.ToArray(),TransportAcceptRetries=Interlocked.Read(ref _transportAcceptRetries), Capabilities=["output-watch","pane-output-watch","navigation-save",HostProtocol.MetadataBatchCapability,"resize-clears-selection","input-modes","row-deltas","pane-themes","appearance-v2","literal-input"] };
        }
        if(r.Operation=="states")
        {
            var versions=r.StateVersions??throw new ArgumentException("State versions are required.");
            if(versions.Count is 0 or >HostProtocol.MaxMetadataPanes || versions.Keys.Any(id=>string.IsNullOrEmpty(id) || id.Length>128))
                throw new ArgumentException("Invalid metadata interests.");
            (string Id,int Seen,Entry? Entry)[] entries;
            lock(_gate) entries=versions.Select(pair=>(pair.Key,pair.Value,_sessions.GetValueOrDefault(pair.Key))).ToArray();
            var states=new Dictionary<string,ShellState>(); var missing=new List<string>();
            foreach(var item in entries)
            {
                if(item.Entry is null) { missing.Add(item.Id); continue; }
                if(item.Entry.Session.Version!=item.Seen) states.Add(item.Id,State(item.Entry));
            }
            return new() { States=states,Panes=missing.ToArray() };
        }
        if(r.Operation=="watch")
        {
            long seen=r.Numbers.FirstOrDefault(-1);
            if(r.WatchPanes is { } interests)
            {
                if(interests.Length is 0 or >256 || interests.Any(id=>string.IsNullOrEmpty(id) || id.Length>128))
                    throw new ArgumentException("Invalid output watch interests.");
                if(r.WatchRevisions is { } revisions && (revisions.Count>256 || revisions.Values.Any(value=>value< -1)))
                    throw new ArgumentException("Invalid output watch revisions.");
                long Seen(string pane)=>r.WatchRevisions?.GetValueOrDefault(pane,-1)??seen;
                Task pulseForPanes;
                lock(_gate) lock(_outputGate)
                {
                    var entries=interests.Distinct().Where(_sessions.ContainsKey).Select(id=>(Id:id,Entry:_sessions[id])).ToArray();
                    pulseForPanes=entries.Any(pair=>pair.Entry.OutputRevision>Seen(pair.Id))?Task.CompletedTask
                        :entries.Length==0?Task.Delay(250,watchCancellation)
                        :Task.WhenAny(entries.Select(pair=>(pair.Entry.OutputPulse??=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task)
                            .Append(Task.Delay(250,watchCancellation)));
                }
                try { await pulseForPanes.WaitAsync(TimeSpan.FromMilliseconds(250),watchCancellation).ConfigureAwait(false); } catch(TimeoutException) { }
                watchCancellation.ThrowIfCancellationRequested();
                lock(_gate) lock(_outputGate)
                {
                    var changed=interests.Distinct().Where(id=>_sessions.TryGetValue(id,out var entry) && entry.OutputRevision>Seen(id)).ToArray();
                    return new() { Numbers=[_outputRevision],Panes=changed,OutputRevisions=changed.ToDictionary(id=>id,id=>_sessions[id].OutputRevision) };
                }
            }
            Task pulse;
            lock(_outputGate) pulse=_outputRevision<=seen?(_outputPulse??=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task:Task.CompletedTask;
            try { await pulse.WaitAsync(TimeSpan.FromSeconds(1),_stop.Token).ConfigureAwait(false); } catch(TimeoutException) { }
            lock(_gate) lock(_outputGate) return new() { Numbers=[_outputRevision],
                Panes=_sessions.Where(pair=>pair.Value.OutputRevision>seen).Select(pair=>pair.Key).ToArray() };
        }
        if(r.Operation=="navigate") return await NavigateAsync(r);
        if(r.Operation=="sync") return await SyncAsync(r);
        if (r.Operation=="shutdown")
        {
            await _layoutWrites.WaitAsync(_stop.Token);
            try
            {
                string[] ids;
                lock(_gate) { ids=_sessions.Keys.ToArray(); _created=true; _emptySince=Environment.TickCount64-2000; }
                foreach(string id in ids) Remove(id);
            }
            finally { _layoutWrites.Release(); }
            return new();
        }
        if (r.Operation=="detach")
        {
            Entry[] entries; lock(_gate) entries=_sessions.Values.ToArray();
            foreach (var entry in entries) { entry.Session.Focus(false); entry.Session.Drop(2); }
            return new();
        }
        if (r.Pane.Length is < 1 or > 128) throw new ArgumentException("Invalid pane identity.");
        if(r.Operation=="create") return await CreateAsync(r);
        if (r.Operation=="close")
        {
            await _layoutWrites.WaitAsync(_stop.Token);
            try { Remove(r.Pane); return new(); }
            finally { _layoutWrites.Release(); }
        }
        Entry e; lock (_gate) e=_sessions.GetValueOrDefault(r.Pane) ?? throw new IOException("This shell session no longer exists.");
        var s=e.Session;
        long N(int index, long fallback=0)=>r.Numbers.Length>index?r.Numbers[index]:fallback;
        switch (r.Operation)
        {
            case "state": return new() { State=State(e) };
            case "capture":
                lock (e.CaptureGate)
                {
                    if(e.Closed) throw new IOException("This shell session has closed.");
                    int version=s.Version;
                    if (N(0,-1)==version && N(1,-1)==e.FrameRevision && Environment.TickCount64<e.AnimationDue) return new() { State=State(e) };
                    bool delta=r.AllowFrameDelta && N(0,-1)>=0 && N(1,-1)==e.FrameRevision && e.CapturedVersion>=0
                        && e.CapturedColumns==e.Columns && e.CapturedRows==e.Rows;
                    long baseline=e.FrameRevision;
                    if (!s.TryReadSnapshot(e.Cells,out var snapshot)) return new() { State=State(e) };
                    delta=delta && snapshot!.Frame.Columns==e.CapturedColumns && snapshot.Frame.Rows==e.CapturedRows && snapshot.ChangedRows.Count<snapshot.Frame.Rows;
                    Captured(e,snapshot!,version);
                    var known=r.Images.ToHashSet();
                    return new() { State=State(e), CapturedVersion=version,FrameRevision=e.FrameRevision,DeltaBaseRevision=delta?baseline:null,
                        Scrollbar=snapshot!.Scrollbar,Binary=delta?HostSnapshotCodec.EncodeDelta(snapshot,e.Cells,known):HostSnapshotCodec.Encode(snapshot,e.Cells,known) };
                }
            case "resize":
                if (N(0) is < 2 or > 1000 || N(1) is < 1 or > 500 || N(2) is < 1 or > 1024 || N(3) is < 1 or > 1024) throw new ArgumentException("Invalid terminal size.");
                lock (e.CaptureGate)
                {
                    if(e.Closed) throw new IOException("This shell session has closed.");
                    ushort columns=(ushort)N(0),rows=(ushort)N(1);
                    var replacement=e.Cells.Length==columns*rows?e.Cells:TerminalCellPool.Shared.Rent(columns*rows);
                    try { s.Resize(columns,rows,(uint)N(2),(uint)N(3)); s.Select(-1,-1); }
                    catch { if(!ReferenceEquals(replacement,e.Cells)) TerminalCellPool.Shared.Return(replacement); throw; }
                    var previous=e.Cells; e.Columns=columns; e.Rows=rows; e.Cells=replacement;
                    if(!ReferenceEquals(previous,replacement)) TerminalCellPool.Shared.Return(previous);
                }
                break;
            case "key": s.Key((uint)N(0),(uint)N(1),r.Text,(TerminalKeyAction)N(2,1),N(3,-1)<0?null:(uint)N(3),(uint)N(4)); break;
            case "paste": s.Paste(r.Text,N(0)!=0); break;
            case "send-text": s.SendText(r.Text); break;
            case "paste-checked": await s.PasteAsync(r.Text); break;
            case "clipboard": s.PasteClipboard(); break;
            case "scroll": s.Scroll((int)N(0)); break;
            case "scroll-to": s.ScrollTo((ulong)Math.Max(0,N(0))); break;
            case "focus": s.Focus(N(0)!=0); if (N(0)!=0) Volatile.Write(ref e.PendingAttention,0); break;
            case "select": s.Select((int)N(0),(int)N(1)); break;
            case "gesture": return new() { Numbers=[s.SelectionGesture((int)N(0),N(1)/1000f,N(2)/1000f,N(3)!=0)] };
            case "drop": return new() { Numbers=[s.Drop((int)N(0),(int)N(1),(int)N(2),r.Items,(uint)N(3,1))] };
            case "drop-async": return new() { Numbers=[await s.DropAsync((int)N(0),(int)N(1),r.Items ?? [],(uint)N(2,1),TimeSpan.FromMilliseconds(Math.Clamp(N(3,15000),1,20000)))] };
            case "copy": return new() { Text=s.CopySelection() };
            case "hyperlink": return new() { Text=s.Hyperlink((ushort)N(0),(ushort)N(1)) };
            case "find": var found=s.Find(r.Text,(int)N(0)); return new() { Numbers=[found.Count,found.Index] };
            case "mouse": return new() { Numbers=[s.Mouse((int)N(0),(int)N(1),(uint)N(2),N(3)/1000f,N(4)/1000f)?1:0] };
            case "settings": s.ApplySettings(r.Settings ?? throw new ArgumentException("Settings are required.")); break;
            default: throw new ArgumentException("Unknown session operation.");
        }
        return new() { State=State(e) };
    }
    private static void ValidateLayout(SavedSession layout)
    {
        if (layout.Version!=5 || layout.Settings is null || layout.Workspaces is null || layout.Workspaces.Count>64) throw new ArgumentException("Invalid session layout.");
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(layout).Length>1024*1024) throw new ArgumentException("Saved sessions exceed the size limit.");
        var identities=new HashSet<string>(); int panes=0;
        foreach (var session in layout.Workspaces)
        {
            if (session.Theme is { IsValid: false }) throw new ArgumentException("Invalid session theme.");
            Add(session.Id); if (session.Tabs is null || session.Tabs.Count is < 1 or > 64) throw new ArgumentException("Invalid session tabs.");
            foreach (var tab in session.Tabs) { Add(tab.Id); Walk(tab.Root,0); }
        }
        void Add(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length>128 || !identities.Add(id)) throw new ArgumentException("Duplicate or invalid session identity."); }
        void Walk(PaneNode node,int depth)
        {
            if (node is null || depth>12 || string.IsNullOrWhiteSpace(node.Command) || node.Kind is not ("terminal" or "browser") ||
                node.BrowserSourceId is null || node.BrowserSourceId.Length>128 || (node.IsBrowser && !BrowserAddress.IsAllowed(node.BrowserUrl))) throw new ArgumentException("Invalid pane tree.");
            if (node.First is null && node.Second is null) { Add(node.Id); if (++panes>256) throw new ArgumentException("Too many panes."); }
            else { if (node.First is null || node.Second is null) throw new ArgumentException("Incomplete pane split."); Walk(node.First,depth+1); Walk(node.Second,depth+1); }
        }
    }
    private void Remove(string id)
    {
        Entry? removed;
        lock(_gate)
        {
            _sessions.Remove(id,out removed);
            if (_sessions.Count==0) _emptySince=Environment.TickCount64;
        }
        if(removed is not null) DisposeEntry(removed);
    }
    private static void DisposeEntry(Entry entry)
    {
        lock(entry.CaptureGate)
        {
            if(entry.Closed) return;
            entry.Closed=true; entry.Session.Dispose();
            TerminalCellPool.Shared.Return(entry.Cells); entry.Cells=[];
        }
    }
    private static void Captured(Entry entry,TerminalSnapshot snapshot,int version)
    {
        entry.LastCapture=Environment.TickCount64; entry.CapturedVersion=version; entry.FrameRevision++;
        entry.CapturedColumns=snapshot.Frame.Columns; entry.CapturedRows=snapshot.Frame.Rows;
        entry.RenderHeld=snapshot.Frame.RenderHeld!=0;
        entry.AnimationDue=snapshot.Frame.AnimationDelay==uint.MaxValue?long.MaxValue:entry.LastCapture+snapshot.Frame.AnimationDelay;
    }
    private async Task MonitorLifetime()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(250,_stop.Token);
                Entry[] entries;
                lock (_gate)
                {
                    entries=_sessions.Values.ToArray();
                    bool live=_sessions.Values.Any(e=>!e.Session.HasExited);
                    if (live) _emptySince=Environment.TickCount64;
                    else if (!_shutdownPending && Environment.TickCount64-_emptySince>(_created?1500:30000)) { _stop.Cancel(); return; }
                }
                try { await PersistDirectoriesAsync(); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
                // Hidden terminals still advance upstream animation clocks and
                // synchronized-output timeouts. A frame revision prevents a
                // window from missing changes captured by this maintenance pass.
                foreach (var entry in entries)
                {
                    lock (entry.CaptureGate)
                    {
                        if (Environment.TickCount64-entry.LastCapture<500) continue;
                        int version=entry.Session.Version;
                        if (version==entry.CapturedVersion && Environment.TickCount64<entry.AnimationDue && !entry.RenderHeld) continue;
                        if (entry.Session.TryReadSnapshot(entry.Cells,out var snapshot)) Captured(entry,snapshot!,version);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); Entry[] entries; lock (_gate) { entries=_sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var entry in entries) { DisposeEntry(entry); await entry.Session.DisposeAsync(); }
        _stop.Dispose(); _connections.Dispose(); _layoutWrites.Dispose();
    }
}
