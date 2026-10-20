using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Toiqtty.Configuration;

// A frontend-owned evaluator, distinct from the host owning persistent shells.
public sealed class LuaRuntimeClient : IDisposable
{
    private readonly Process _process;
    private readonly ConfigWorkerLimits _limits;
    private readonly BoundedLines _output;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private LuaRuntimeClient(Process process, ConfigWorkerLimits limits) { _process=process; _limits=limits; _output=new(process.StandardOutput); }
    public LuaConfigResult Config { get; private set; } = new();
    internal int ProcessIdForCheck => _process.Id;
    public static async Task<LuaRuntimeClient> Start(LuaConfigRequest request, string? helper=null, CancellationToken cancellation=default)
    {
        var process = new Process { StartInfo = new(helper ?? LuaConfigService.HelperPath) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8 } };
        process.StartInfo.ArgumentList.Add("--runtime-config");
        ConfigWorkerLimits? limits=null; LuaRuntimeClient? client=null;
        try
        {
            process.Start(); limits=ConfigWorkerLimits.Attach(process); client=new(process,limits);
            client.Config=await client.Exchange(request,ConfigurationJsonContext.Default.LuaConfigRequest,ConfigurationJsonContext.Default.LuaConfigResult,TimeSpan.FromSeconds(4),cancellation);
            if (client.Config.Error is null) LuaConfigParser.Validate(client.Config.Settings);
            return client;
        }
        catch { if(client is not null) client.Dispose(); else { limits?.Dispose(); try { if(!process.HasExited) process.Kill(); } catch(InvalidOperationException){} process.Dispose(); } throw; }
    }
    public async Task<LuaPresentationResult> Invoke(LuaPresentationRequest request, CancellationToken cancellation=default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Exchange(request,ConfigurationJsonContext.Default.LuaPresentationRequest,ConfigurationJsonContext.Default.LuaPresentationResult,TimeSpan.FromMilliseconds(500),cancellation).ConfigureAwait(false); }
        catch { Dispose(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<T> Exchange<TRequest,T>(TRequest request,JsonTypeInfo<TRequest> requestInfo,JsonTypeInfo<T> responseInfo,TimeSpan timeout,CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(timeout);
        string json=JsonSerializer.Serialize(request,requestInfo);
        if(json.Length>512*1024) throw new ArgumentException("Runtime request is too large.");
        await _process.StandardInput.WriteLineAsync(json.AsMemory(),deadline.Token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
        string response=await _output.Read(2*1024*1024,deadline.Token).ConfigureAwait(false) ?? throw new IOException("Lua worker exited unexpectedly.");
        return JsonSerializer.Deserialize(response,responseInfo) ?? throw new IOException("Empty Lua runtime result.");
    }
    private sealed class BoundedLines(TextReader reader)
    {
        private readonly char[] _buffer=new char[4096];
        private int _position,_count;
        internal async Task<string?> Read(int limit,CancellationToken cancellation)
        {
            var text=new StringBuilder();
            while(true)
            {
                if(_position==_count)
                {
                    _count=await reader.ReadAsync(_buffer.AsMemory(),cancellation).ConfigureAwait(false); _position=0;
                    if(_count==0) return text.Length==0?null:text.ToString();
                }
                int newline=Array.IndexOf(_buffer,'\n',_position,_count-_position);
                int length=(newline<0?_count:newline)-_position;
                if(text.Length+length>limit) throw new IOException("Lua worker message exceeds its limit.");
                text.Append(_buffer,_position,length); _position+=length;
                if(newline>=0) { _position++; return text.ToString(); }
            }
        }
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        _limits.Dispose(); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ends only this evaluator.
        _process.Dispose();
    }
    public static async Task RunWorker()
    {
        Console.InputEncoding=Encoding.UTF8; Console.OutputEncoding=new UTF8Encoding(false);
        LuaConfigEvaluation evaluation;
        var input=new BoundedLines(Console.In);
        try
        {
            var request=JsonSerializer.Deserialize(await input.Read(512*1024,CancellationToken.None) ?? "",ConfigurationJsonContext.Default.LuaConfigRequest) ?? throw new ArgumentException("Missing runtime configuration.");
            evaluation=await LuaConfigParser.EvaluateRuntime(request);
            string json=JsonSerializer.Serialize(evaluation.Result,ConfigurationJsonContext.Default.LuaConfigResult);
            if(json.Length>2*1024*1024) throw new ArgumentException("Lua configuration result is too large.");
            await Console.Out.WriteLineAsync(json); await Console.Out.FlushAsync();
        }
        catch(Exception ex) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new LuaConfigResult { Error=ex.Message[..Math.Min(ex.Message.Length,8192)] },ConfigurationJsonContext.Default.LuaConfigResult)); return; }
        while(await input.Read(512*1024,CancellationToken.None) is { } line)
        {
            LuaPresentationResult result;
            try { result=await evaluation.Invoke(JsonSerializer.Deserialize(line,ConfigurationJsonContext.Default.LuaPresentationRequest) ?? throw new ArgumentException("Missing runtime context.")); }
            catch(Exception ex) { result=new() { Error=ex.Message[..Math.Min(ex.Message.Length,8192)] }; }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result,ConfigurationJsonContext.Default.LuaPresentationResult)); await Console.Out.FlushAsync();
        }
    }
}
