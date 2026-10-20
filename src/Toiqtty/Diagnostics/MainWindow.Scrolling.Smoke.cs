using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media.Imaging;
using Toiqtty.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunScrollingProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        var initial=_selected!;
        NewWorkspace(ShellDefaults.Command+" -NoLogo -NoProfile","Scrolling"); RemoveWorkspace(initial);
        var workspace=_selected!; var shell=workspace.Root.Session!;
        await wait(()=>_focused is { FramesPainted:>0 });
        var view=_focused!; string size=view.ViewportLabel, paneId=view.PaneId;
        string token=Guid.NewGuid().ToString("N");
        shell.Paste("1..400 | ForEach-Object { Write-Output ('SCROLL_LINE_'+$_) }; Write-Output 'SCROLL_DONE_"+token+"'"); shell.Key(13,0);
        await wait(()=>view.ScrollStateForCheck is { Maximum:>200 } && view.RenderedText.Contains("SCROLL_DONE_"+token));
        var state=view.ScrollStateForCheck!; var bar=view.ScrollbarForCheck;
        check(bar.IsEnabled && bar.Maximum==state.Maximum && bar.Value==state.Offset && bar.ViewportSize==state.Visible,
            "native WinUI scrollbar reflects Ghostty history, position and visible rows through the session host");
        check(size==view.ViewportLabel,"the scrollbar gutter does not reflow the terminal when history appears");
        var originalScrolling = _saved.Settings.Scrolling;
        _saved.Settings.Scrolling = new() { LinesPerNotch = 7 };
        view.ApplySettings();
        ulong wheelStart = view.ScrollStateForCheck!.Offset;
        view.RouteWheel(60, new Point(80,100));
        await Task.Delay(100);
        check(view.ScrollStateForCheck!.Offset == wheelStart, "partial wheel input never sends Scroll(0) or moves the viewport");
        view.RouteWheel(60, new Point(80,100));
        await wait(() => view.ScrollStateForCheck!.Offset == wheelStart - 7);
        check(true, "the configured wheel speed scrolls exactly seven history rows per accumulated notch");
        _saved.Settings.Scrolling = new() { LinesPerNotch = 7, Reverse = true };
        view.ApplySettings();
        view.RouteWheel(120, new Point(80,100));
        await wait(() => view.ScrollStateForCheck!.Offset == wheelStart);
        check(true, "live wheel direction changes reverse local scrolling");
        _saved.Settings.Scrolling = originalScrolling; view.ApplySettings();
        var pointerEvents=new List<string>();
        view.ValidationObserved+=(kind,detail)=> { if(kind.StartsWith("autoscroll")) { pointerEvents.Add(kind+":"+detail); File.WriteAllLines(Path.Combine(SessionStore.Folder,"autoscroll-events.log"),pointerEvents); } };
        var anchor=new Point(80,100);
        view.BeginMiddleScroll(anchor);
        view.MiddleScrollCaptureLost(false);
        check(view.AutoScrollActive && view.MiddleScrollForCheck.Latched,"capture loss before button release latches a stationary middle click");
        view.ReleaseMiddleScrollAt(anchor);
        view.MiddleScrollPointerExited(anchor);
        view.MiddleScrollCaptureLost(false);
        await Task.Delay(250);
        check(view.AutoScrollActive,"release and in-pane exit notifications preserve latched autoscroll: "+string.Join("; ",pointerEvents));
        check(!view.MiddleScrollKeyDown(16) && !view.MiddleScrollKeyDown(160) && !view.MiddleScrollKeyDown(161) && view.AutoScrollActive,
            "Shift and either physical Shift key leave autoscroll active");
        view.MoveMiddleScroll(new Point(80,20));
        ulong startingOffset=view.ScrollStateForCheck!.Offset;
        await wait(()=>view.ScrollStateForCheck!.Offset<startingOffset-2);
        await Task.Delay(650);
        check(view.AutoScrollActive,"latched scrolling survives successive host snapshots and thumb updates: "+string.Join("; ",pointerEvents));
        view.MiddleScrollPointerExited(new Point(-1,100));
        check(!view.AutoScrollActive,"leaving the actual pane bounds cancels latched scrolling");
        view.BeginMiddleScroll(anchor); view.MoveMiddleScroll(new Point(80,20));
        view.MiddleScrollCaptureLost(false);
        check(!view.AutoScrollActive,"button-up capture loss stops a held middle-button drag");
        view.BeginMiddleScroll(anchor); view.MiddleScrollCaptureLost(true);
        check(!view.AutoScrollActive,"unexpected capture loss while the button is held still cancels safely");
        view.BeginMiddleScroll(anchor);
        check(view.MiddleScrollKeyDown(27) && !view.AutoScrollActive,"Escape cancels autoscroll and is consumed before reaching the shell");
        view.FocusTerminal(); view.BeginMiddleScroll(anchor);
        NewTabButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        await wait(()=>!view.AutoScrollActive);
        check(true,"moving keyboard focus to an outside control cancels autoscroll after focus settles");
        view.FocusTerminal();
        File.WriteAllText(Path.Combine(SessionStore.Folder,"pointer-lifecycle.json"),System.Text.Json.JsonSerializer.Serialize(pointerEvents));
        var peer=FrameworkElementAutomationPeer.CreatePeerForElement(bar)!;
        var range=(IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue);
        range.SetValue(0);
        await wait(()=>view.ScrollStateForCheck?.Offset==0 && view.RenderedText.Contains("SCROLL_LINE_1"));
        check(bar.Value==0,"native scrollbar accessibility actions scroll to the oldest rendered history");
        double middle=Math.Round(bar.Maximum/2); range.SetValue(middle);
        await wait(()=>view.ScrollStateForCheck?.Offset==(ulong)middle);
        check(bar.Value==middle,"dragging or setting a thumb position maps to an absolute Ghostty history row");
        shell.Scroll(-5); await wait(()=>view.ScrollStateForCheck?.Offset==(ulong)middle-5);
        check(bar.Value==middle-5,"wheel-style relative scrolling updates the native thumb without feedback loops");
        ulong unchanged=view.ScrollStateForCheck!.Offset;
        view.BeginMiddleScroll(new Point(80,100)); await Task.Delay(180);
        check(view.AutoScrollActive && view.ScrollStateForCheck?.Offset==unchanged,"stationary middle-click autoscroll preserves the current scrollback position");
        await Capture();
        view.MiddleScrollForCheck.Move(20);
        await wait(()=>view.ScrollStateForCheck!.Offset<unchanged-2);
        view.MiddleScrollForCheck.Release(); view.StopAutoScroll();
        check(!view.AutoScrollActive,"middle-button drag scrolls real host history and stops on release");
        view.BeginMiddleScroll(new Point(80,100)); view.MiddleScrollForCheck.Release(); view.MiddleScrollForCheck.Move(180);
        ulong start=view.ScrollStateForCheck!.Offset;
        await wait(()=>view.ScrollStateForCheck!.Offset>start+2);
        view.IsActive=false;
        check(!view.AutoScrollActive,"latched browser-style autoscroll stops when its pane becomes inactive");
        view.IsActive=true; range.SetValue(bar.Maximum);
        await wait(()=>view.ScrollStateForCheck!.Offset==view.ScrollStateForCheck.Maximum);
        check(view.RenderedText.Contains("SCROLL_DONE_"+token),"scrolling to the bottom returns to the live PowerShell prompt");
        var session=workspace.Root.Session;
        Split(true); await wait(()=>_views.Count==2 && _views.All(v=>v.FramesPainted>0));
        check(_views.Single(v=>v.PaneId==paneId).ScrollbarForCheck.Maximum>200 && _focused!.ScrollbarForCheck.Maximum==0,
            "split panes have independent scrollbar ranges and fresh shells have no fabricated history");
        check(ReferenceEquals(workspace.AllPanes().Single(p=>p.Id==paneId).Session,session),"scrolling and splitting preserve the original live shell object");
        view=_views.Single(v=>v.PaneId==paneId);
        view.BeginMiddleScroll(new Point(80,100));
        NewTab("cmd.exe /Q /K");
        check(!view.AutoScrollActive,"switching tabs disposes the old view and cancels its autoscroll timer");
        RemoveWorkspace(workspace);
        string client=Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE") ?? throw new IOException("Scrolling input fixture is required.");
        NewWorkspace("\""+client+"\" --scroll-input-client","Application scrolling");
        var appWorkspace=_selected!;
        await wait(()=>_focused?.RenderedText.Contains("APP_WHEEL_READY")==true);
        var appView=_focused!;
        check(appView.ScrollStateForCheck is { Maximum:0 },"the mouse-reporting fixture owns a real alternate screen without terminal scrollback");
        _saved.Settings.Scrolling = new() { AppEventsPerNotch = 4 };
        appView.ApplySettings();
        appView.RouteWheel(240, anchor);
        await wait(() => AppCount("APP_WHEEL_UP=") == 8);
        check(AppCount("APP_WHEEL_DOWN=") == 0, "two wheel notches deliver exactly eight configured app reports");
        _saved.Settings.Scrolling = new() { AppEventsPerNotch = 2, Reverse = true };
        appView.ApplySettings(); appView.RouteWheel(120, anchor);
        await wait(() => AppCount("APP_WHEEL_DOWN=") == 2);
        appView.RouteWheel(120, anchor, forceHistory: true);
        await Task.Delay(150);
        check(AppCount("APP_WHEEL_UP=") == 8 && AppCount("APP_WHEEL_DOWN=") == 2,
            "reversed app wheel reports preserve direction and forced local scrolling sends no app input");
        _saved.Settings.Scrolling = originalScrolling; appView.ApplySettings();
        appView.BeginMiddleScroll(anchor); appView.ReleaseMiddleScrollAt(anchor);
        appView.MoveMiddleScroll(new Point(80,20));
        await wait(()=>AppCount("APP_WHEEL_UP=")>=11);
        appView.StopAutoScroll();
        check(appView.ScrollStateForCheck is { Maximum:0,Offset:0 } && AppCount("APP_WHEEL_DOWN=")==2,
            "autoscroll up reaches the application's raw input as wheel-up reports without inventing terminal history");
        appView.BeginMiddleScroll(anchor); appView.ReleaseMiddleScrollAt(anchor);
        appView.MoveMiddleScroll(new Point(80,180));
        await wait(()=>AppCount("APP_WHEEL_DOWN=")>=3);
        appView.StopAutoScroll();
        check(appView.ScrollStateForCheck is { Maximum:0,Offset:0 },"autoscroll down reaches the application's raw input as wheel-down reports");
        check(AppCount("APP_MIDDLE=")==0,"the forced autoscroll gesture does not send accidental middle clicks to the application");
        RemoveWorkspace(appWorkspace);

        int AppCount(string prefix)
        {
            var match=System.Text.RegularExpressions.Regex.Match(appView.RenderedText,System.Text.RegularExpressions.Regex.Escape(prefix)+@"(\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        async Task Capture()
        {
            var bitmap=new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
            var buffer=await bitmap.GetPixelsAsync(); byte[] pixels=new byte[buffer.Length];
            using(var reader=DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            string path=Path.Combine(SessionStore.Folder,"scrolling.png"); File.WriteAllBytes(path,[]);
            var file=await StorageFile.GetFileFromPathAsync(path);
            using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            double scale=Root.XamlRoot.RasterizationScale;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96*scale,96*scale,pixels);
            await encoder.FlushAsync();
        }
    }

}
