using System.Diagnostics;
using System.Text.Json;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed class EnduranceInterruptedException(string message):OperationCanceledException(message);
    private Stopwatch? _enduranceClock;
    private bool _enduranceExpectedHidden;
    private double _endurancePausedSeconds;
    private int _endurancePauseCount;

    private async Task WaitForEndurancePresentation()
    {
        if(_enduranceExpectedHidden || WindowRenderingActive) return;
        _enduranceClock?.Stop(); _endurancePauseCount++;
        var pause=Stopwatch.StartNew(); long nextReport=0;
        void Report(bool paused)
        {
            string path=Path.Combine(SessionStore.Folder,"endurance-pause.json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new { timestamp=DateTimeOffset.Now,paused,
                pausedSeconds=_endurancePausedSeconds+(paused?pause.Elapsed.TotalSeconds:0),pauseCount=_endurancePauseCount,
                reason="External hide/minimize or power suspension; onscreen assertions and active stress time are paused." }));
            File.Move(path+".tmp",path,true);
        }
        try
        {
            while(!WindowRenderingActive)
            {
                if(_closed || _closing) throw new EnduranceInterruptedException("The isolated endurance window was closed while externally suspended.");
                if(_endurancePausedSeconds+pause.Elapsed.TotalSeconds>=1800) throw new EnduranceInterruptedException("The isolated endurance window accumulated thirty minutes of external suspension.");
                if(pause.ElapsedMilliseconds>=nextReport) { Report(true); nextReport=pause.ElapsedMilliseconds+1000; }
                await Task.Delay(100);
            }
        }
        finally
        {
            _endurancePausedSeconds+=pause.Elapsed.TotalSeconds; Report(false); _enduranceClock?.Start();
        }
    }

    private async Task CheckEndurancePause(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        var presenter=(Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        _enduranceExpectedHidden=true;
        presenter.Minimize(false); await wait(()=>_windowPresentationHidden);
        _enduranceExpectedHidden=false;
        var clock=_enduranceClock=Stopwatch.StartNew();
        async Task Restore() { await Task.Delay(1500); presenter.Restore(false); }
        var restore=Restore(); await WaitForEndurancePresentation(); await restore;
        clock.Stop();
        check(_endurancePauseCount==1 && _endurancePausedSeconds>=1.2,"External minimization pauses onscreen assertions until the isolated window is restored");
        check(clock.Elapsed.TotalMilliseconds<500,"External suspension is excluded from the active endurance clock");
        check(WindowRenderingActive,"Restoring an externally minimized fixture resumes normal presentation");
        File.WriteAllText(Path.Combine(SessionStore.Folder,"endurance-pause-check.json"),JsonSerializer.Serialize(new {
            passed=true,pausedSeconds=_endurancePausedSeconds,activeMilliseconds=clock.Elapsed.TotalMilliseconds,pauseCount=_endurancePauseCount }));
        _enduranceClock=null; _endurancePausedSeconds=0; _endurancePauseCount=0;
        string pausePath=Path.Combine(SessionStore.Folder,"endurance-pause.json");
        File.WriteAllText(pausePath+".tmp",JsonSerializer.Serialize(new { paused=false,pausedSeconds=0,pauseCount=0 })); File.Move(pausePath+".tmp",pausePath,true);
    }

    private async Task<T> WaitForProbePresentation<T>(Task<T> response)
    {
        if(_enduranceClock is null) return await response.WaitAsync(TimeSpan.FromSeconds(5));
        var timeout=Stopwatch.StartNew();
        while(!response.IsCompleted)
        {
            if(!_enduranceExpectedHidden && !WindowRenderingActive)
            { timeout.Stop(); await WaitForEndurancePresentation(); timeout.Start(); }
            if(timeout.Elapsed.TotalSeconds>5) throw new TimeoutException("The response frame was not presented during five active seconds.");
            await Task.WhenAny(response,Task.Delay(50));
        }
        return await response;
    }
}
