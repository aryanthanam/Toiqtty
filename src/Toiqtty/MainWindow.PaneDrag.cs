using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Controls;
using Toiqtty.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private const string PaneMoveFormat="Toiqtty.PaneMove.v1";
    private string? _draggedPaneId, _paneDragToken;
    private bool _paneFocusPending;
    private readonly Dictionary<string,(Grid Host, Grid HitArea, Border Preview, ContentControl Handle)> _paneDragSurfaces=[];
    private enum PaneEdge { Left, Right, Top, Bottom }
    private sealed record PaneDestination(string? Workspace, string? Tab, string? Pane=null, PaneEdge Edge=PaneEdge.Right);

    private void InitializePaneDragging()
    {
        WirePaneDestination(NewTabButton,()=>_selected is { } session ? new(session.Id,null) : null,"Move into a new tab");
    }

    private FrameworkElement WrapPane(FrameworkElement view, PaneNode pane)
    {
        var host=new Grid(); host.Children.Add(view);
        uint accent = Chrome.For(ThemeFor(_selected)).Accent;
        var preview=new Border { IsHitTestVisible=false, Visibility=Visibility.Collapsed,
            Background=Chrome.Brush(0x33000000 | (accent & 0xffffff)), BorderBrush=Chrome.Brush(accent), BorderThickness=new Thickness(2),
            Child=new TextBlock { Foreground=Chrome.Brush(Chrome.Text), FontSize=13,
                HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center } };
        var hitArea=new Grid { AllowDrop=true, Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility=Visibility.Collapsed };
        hitArea.Children.Add(preview); host.Children.Add(hitArea);
        var handle=new ContentControl { CanDrag=true, IsTabStop=true, Width=28, Height=28, Opacity=0,
            HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(4),
            HorizontalContentAlignment=HorizontalAlignment.Center, VerticalContentAlignment=VerticalAlignment.Center,
            Background=Chrome.Brush(Chrome.Background), CornerRadius=new CornerRadius(3),
            Content=new FontIcon { Glyph="\uE784", FontSize=13, Foreground=Chrome.Brush(Chrome.Secondary) } };
        AutomationProperties.SetName(handle,"Move pane"); AutomationProperties.SetAutomationId(handle,"PaneDrag-"+pane.Id);
        AutomationProperties.SetHelpText(handle,"Drag to a pane edge, tab, or new-tab button in this session. Press Enter for move options.");
        ToolTipService.SetToolTip(handle,"Drag pane · Enter for move options");
        host.Children.Add(handle);
        _paneDragSurfaces[pane.Id]=(host,hitArea,preview,handle);
        bool hovering=false;
        host.PointerEntered+=(_,_)=> { hovering=true; handle.Opacity=.85; };
        host.PointerExited+=(_,_)=> { hovering=false; handle.Opacity=handle.FocusState==FocusState.Unfocused ? 0 : 1; };
        handle.GotFocus+=(_,_)=>handle.Opacity=1;
        handle.LostFocus+=(_,_)=>handle.Opacity=hovering ? .85 : 0;
        handle.Tapped+=(_,_)=>FocusPane(pane.Id);
        handle.KeyDown+=(_,e)=> { if(e.Key is VirtualKey.Enter or VirtualKey.Space){e.Handled=true; _=MovePaneDialog(pane.Id);} };
        handle.DragStarting+=(_,e)=>
        {
            if(!BeginPaneDrag(pane.Id,e.Data)){e.Cancel=true;return;}
        };
        handle.DropCompleted+=(_,_)=>EndPaneDrag();
        hitArea.DragOver+=(_,e)=>
        {
            if(!OwnPaneDrag(e.DataView)) return;
            e.Handled=true;
            var edge=DropEdge(e.GetPosition(host),host.ActualWidth,host.ActualHeight);
            var destination=DestinationForPane(pane.Id,edge);
            bool valid=CanDropPane(destination);
            e.AcceptedOperation=valid ? DataPackageOperation.Move : DataPackageOperation.None;
            if(valid){ShowPanePreview(pane.Id,edge); e.DragUIOverride.Caption="Split "+edge.ToString().ToLowerInvariant();}
            else preview.Visibility=Visibility.Collapsed;
        };
        hitArea.DragLeave+=(_,_)=>preview.Visibility=Visibility.Collapsed;
        hitArea.Drop+=(_,e)=>
        {
            if(!OwnPaneDrag(e.DataView)) return;
            e.Handled=true;
            var destination=DestinationForPane(pane.Id,DropEdge(e.GetPosition(host),host.ActualWidth,host.ActualHeight));
            e.AcceptedOperation=CompletePaneDrop(e.DataView,destination) ? DataPackageOperation.Move : DataPackageOperation.None;
        };
        host.Loaded+=(_,_)=> { if(_selected?.FocusedPaneId==pane.Id) QueuePaneFocus(); };
        return host;
    }

    private bool BeginPaneDrag(string paneId, DataPackage data)
    {
        if(_closed || _closing || _dialogOpen || _draggedPaneId is not null || _draggingTabs || _draggingSessions || SessionLayout.Find(_saved,paneId) is null) return false;
        _pendingTabActivation=null; _draggedPaneId=paneId; _paneDragToken=Guid.NewGuid().ToString("N");
        data.SetData(PaneMoveFormat,_paneDragToken); data.Properties[PaneMoveFormat]=_paneDragToken;
        data.RequestedOperation=DataPackageOperation.Move; data.Properties.Title="Move pane";
        foreach (var (_, HitArea, _, _) in _paneDragSurfaces.Values) HitArea.Visibility=Visibility.Visible;
        return true;
    }
    private bool OwnPaneDrag(DataPackageView data) => !_closed && !_closing && _draggedPaneId is not null && _paneDragToken is not null
        && data.Contains(PaneMoveFormat) && data.Properties.TryGetValue(PaneMoveFormat,out var token) && Equals(token,_paneDragToken);

    private PaneDestination? DestinationForPane(string paneId,PaneEdge edge)
        => SessionLayout.Find(_saved,paneId) is { } target ? new(target.Workspace.Id,target.Tab.Id,paneId,edge) : null;
    private bool CanDropPane(PaneDestination? destination) => _draggedPaneId is { } pane && destination is { } target
        && SessionLayout.CanMovePane(_saved,pane,target.Workspace,target.Tab,target.Edge is PaneEdge.Left or PaneEdge.Right,
            out _,target.Pane,target.Edge is PaneEdge.Left or PaneEdge.Top);

    private bool CompletePaneDrop(DataPackageView data,PaneDestination? destination)
    {
        if(!OwnPaneDrag(data) || !CanDropPane(destination)) return false;
        string pane=_draggedPaneId!; var target=destination!;
        EndPaneDrag(false);
        // Drop callbacks still belong to the old visual tree. Rebuild after
        // native drop processing, without capturing a soon-to-be-disposed view.
        return DispatcherQueue.TryEnqueue(()=>
        {
            if(!_closed) MovePane(pane,target.Workspace,target.Tab,target.Edge is PaneEdge.Left or PaneEdge.Right,
                target.Pane,target.Edge is PaneEdge.Left or PaneEdge.Top);
        });
    }
    private void EndPaneDrag(bool refresh=true)
    {
        _draggedPaneId=null; _paneDragToken=null;
        foreach (var (_, HitArea, Preview, _) in _paneDragSurfaces.Values){HitArea.Visibility=Visibility.Collapsed;Preview.Visibility=Visibility.Collapsed;}
        if(!refresh || _closed) return;
        RefreshSidebar(); RefreshTabs(); QueuePaneFocus();
    }
    private void WirePaneDestination(FrameworkElement element,Func<PaneDestination?> target,string caption)
    {
        element.AllowDrop=true;
        // ListViewItem and Button class handlers can consume drag events first.
        element.AddHandler(UIElement.DragOverEvent,new DragEventHandler((_,e)=>
        {
            if(!OwnPaneDrag(e.DataView)) return;
            e.Handled=true;
            bool valid=CanDropPane(target());
            e.AcceptedOperation=valid ? DataPackageOperation.Move : DataPackageOperation.None;
            if(valid) e.DragUIOverride.Caption=caption;
        }),true);
        element.AddHandler(UIElement.DropEvent,new DragEventHandler((_,e)=>
        {
            if(!OwnPaneDrag(e.DataView)) return;
            e.Handled=true;
            e.AcceptedOperation=CompletePaneDrop(e.DataView,target()) ? DataPackageOperation.Move : DataPackageOperation.None;
        }),true);
    }
    private static PaneEdge DropEdge(Point point,double width,double height)
    {
        double x=Math.Clamp(point.X/Math.Max(1,width),0,1), y=Math.Clamp(point.Y/Math.Max(1,height),0,1);
        if(x>.25 && x<.75 && y>.25 && y<.75) return PaneEdge.Right;
        double distance=Math.Min(Math.Min(x,1-x),Math.Min(y,1-y));
        return distance==x ? PaneEdge.Left : distance==1-x ? PaneEdge.Right : distance==y ? PaneEdge.Top : PaneEdge.Bottom;
    }
    private void ShowPanePreview(string paneId,PaneEdge edge)
    {
        var (host,_,preview,_)=_paneDragSurfaces[paneId];
        bool vertical=edge is PaneEdge.Left or PaneEdge.Right;
        preview.Width=host.ActualWidth*(vertical ? .5 : 1); preview.Height=host.ActualHeight*(vertical ? 1 : .5);
        preview.HorizontalAlignment=edge==PaneEdge.Left ? HorizontalAlignment.Left : edge==PaneEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        preview.VerticalAlignment=edge==PaneEdge.Top ? VerticalAlignment.Top : edge==PaneEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        ((TextBlock)preview.Child).Text="Split "+edge.ToString().ToLowerInvariant(); preview.Visibility=Visibility.Visible;
    }
    private void FocusPane(string paneId)
    {
        if(_views.FirstOrDefault(v=>v.PaneId==paneId) is { } terminal) terminal.FocusTerminal();
        else if(_browsers.TryGetValue(paneId,out var browser) && browser.Parent is not null) browser.FocusBrowser();
    }
    private void QueuePaneFocus()
    {
        if(_closed || _closing || _paneFocusPending) return;
        _paneFocusPending=DispatcherQueue.TryEnqueue(()=>
        {
            _paneFocusPending=false;
            if(_closed || _closing || !WindowRenderingActive || _draggedPaneId is not null || _selected is null) return;
            string? id=_selected.FocusedPaneId;
            if(_views.FirstOrDefault(v=>v.PaneId==id && v.IsLoaded) is { } terminal) terminal.FocusTerminal();
            else if(id is not null && _browsers.TryGetValue(id,out var browser) && browser.Parent is not null && browser.IsLoaded) browser.FocusBrowser();
            else if(id is null && _views.FirstOrDefault(v=>v.IsLoaded) is { } first) first.FocusTerminal();
        });
    }
}
