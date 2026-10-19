using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Oroterm.Hosting;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunResizeProbe(Action<bool,string> check,Func<Func<bool>,Task> wait)
    {
        string client=Environment.GetEnvironmentVariable("OROTERM_SMOKE_EXE")!;
        string command=$"\"{client}\" --resize-client";
        var initial=_selected!;
        _saved.Settings.Shell=command;
        NewWorkspace(command,"Resize regression"); RemoveWorkspace(initial);
        await wait(()=>_focused is { FramesPainted:>0 });
        Split(true);
        await wait(()=>_views.Count==2 && _focused is { FramesPainted:>0 });
        Split(false);
        await wait(()=>_views.Count==3 && _views.All(v=>v.RenderedText.Contains("DPI_CHECK") && v.HasImage(901) && !v.ResizePendingForCheck));
        check(_selected!.AllPanes().Select(p=>p.Session).OfType<RemoteTerminalSession>().All(s=>s.ViewersForCheck==1 && s.FrameListenersForCheck==1),
            "Splitting detaches the original view and retains exactly one viewer and frame listener per pane");
        var identity=_host!.Identity;
        var paneIds=_host.Call(new()).Panes.Order().ToArray();
        var view=_views[0];
        var remote=(RemoteTerminalSession)_selected!.AllPanes().Single(p=>p.Id==view.PaneId).Session!;
        long sent=Interlocked.Read(ref remote.ResizeRequestsSent);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<1000;i++) remote.QueueResize((ushort)(80+i%70),(ushort)(20+i%30),8,20);
        watch.Stop();
        check(watch.Elapsed<TimeSpan.FromSeconds(1),"A burst of 1,000 host resize requests returns without waiting on the UI thread");
        view.ApplySettings();
        await wait(()=>!remote.ResizePending && !view.ResizePendingForCheck);
        check(Interlocked.Read(ref remote.ResizeRequestsSent)-sent<1000,"Rapid host resize requests merge instead of creating an unbounded queue");

        var splits=Descendants(PaneHost).OfType<Grid>().Where(g=>g.Children.Count==3 && g.Children[1] is Thumb
            && (g.ColumnDefinitions.Count==3 || g.RowDefinitions.Count==3)).ToArray();
        check(splits.Length==2,"The resize fixture exercises nested horizontal and vertical pane splitters");
        view.HoldSnapshotsForCheck=true;
        string retainedText=view.RenderedText;
        using(var target=view.CreateRenderTargetForCheck())
        {
            view.RenderFrameForCheck(target);
            var before=target.GetPixelBytes();
            foreach(var grid in splits) SetRatio(grid,.27);
            await wait(()=>view.ResizePendingForCheck);
            long frames=view.FramesPainted;
            view.RenderFrameForCheck(target);
            var after=target.GetPixelBytes();
            // The fixture parks the cursor below this region. Compare actual
            // image/text pixels, rather than only the retained managed snapshot.
            int prefix=Math.Min(before.Length,(int)target.SizeInPixels.Width*4*140);
            check(view.FramesPainted>frames && before.AsSpan(0,prefix).SequenceEqual(after.AsSpan(0,prefix))
                && retainedText==view.RenderedText,"Text and Kitty image pixels remain visible while the resized host frame is delayed");
        }
        view.HoldSnapshotsForCheck=false;
        await wait(()=>_views.All(v=>!v.ResizePendingForCheck));
        var commits=_views.Sum(v=>v.ResizeCommits);
        for(int i=0;i<360;i++)
        {
            foreach(var grid in splits) SetRatio(grid,.1+(i*37%81)/100d);
            Root.UpdateLayout();
            if(i%6==0) await Task.Delay(16);
        }
        foreach(var grid in splits) SetRatio(grid,.5);
        Root.UpdateLayout();
        await wait(()=>_views.All(v=>!v.ResizePendingForCheck));
        // Shrinking to one row can legitimately scroll the fixture and image
        // out of view. Ask the test app to redraw through the real input path.
        foreach(var pane in _selected.AllPanes()) pane.Session!.Key(82,0,"r");
        await wait(()=>_views.All(v=>!v.ResizePendingForCheck && v.RenderedText.Contains("DPI_CHECK") && v.HasImage(901)));
        check(_views.Sum(v=>v.ResizeCommits)-commits<360*3,"Rapid layout changes coalesce to presentation ticks across all three panes");
        check(_views.All(v=>v.PhysicalCellsAreIntegral && v.InputCaretIsAligned),"Resized frames retain integral physical cells and aligned IME carets");
        check(_views.All(v=>v.PointerGeometryIsAligned),"Engine cell sizes match the rendered grid after rapid pane resizing");
        foreach(var paneView in _views)
        {
            var paneSession=(RemoteTerminalSession)paneView.Session!;
            foreach(var sample in new[] { (Row:0, Text:"DPI_CHECK"), (Row:8, Text:"CARET>") })
            {
                paneView.DragSelectionForCheck(sample.Row,0,sample.Text.Length-1);
                await paneSession.InputDrained;
                check(await paneSession.CopySelectionAsync()==sample.Text,
                    $"Pointer drags select the displayed text in row {sample.Row+1} after split resizing: {paneView.PaneId}");
            }
            paneSession.Select(-1,-1);
        }
        check(_host.Identity==identity && _host.Call(new()).Panes.Order().SequenceEqual(paneIds),"Resizing preserves the host and all shell pane identities");
        foreach(var pane in _views)
            check(await pane.CheckRenderedImage(901,Windows.UI.Color.FromArgb(255,255,0,0),Path.Combine(Models.SessionStore.Folder,"resized-"+pane.PaneId+".png")),"Shell input and Kitty image rendering work after rapid pane resizing: "+pane.PaneId);

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                yield return child;
                foreach(var descendant in Descendants(child)) yield return descendant;
            }
        }
        static void SetRatio(Grid grid,double ratio)
        {
            if(grid.ColumnDefinitions.Count==3)
            { grid.ColumnDefinitions[0].Width=new(ratio,GridUnitType.Star); grid.ColumnDefinitions[2].Width=new(1-ratio,GridUnitType.Star); }
            else
            { grid.RowDefinitions[0].Height=new(ratio,GridUnitType.Star); grid.RowDefinitions[2].Height=new(1-ratio,GridUnitType.Star); }
        }
    }
}
