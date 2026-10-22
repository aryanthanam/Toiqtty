using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Toiqtty.Controls;

public sealed partial class TerminalView
{
    private bool _dropHoverPending;
    private int _dropGeneration;
    private DataPackageOperation _dropAccepted;

    private void InitializeDrop()
    {
        AllowDrop = true;
        DragOver += DragOverOnView;
        DragLeave += DragLeaveOnView;
        Drop += DropOnView;
    }

    private void DragOverOnView(object sender, DragEventArgs e) => UiTasks.Observe(DragOverOnViewAsync(sender, e));
    private async Task DragOverOnViewAsync(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = _dropAccepted;
        if (_dropHoverPending || _session is null) return;
        _dropHoverPending = true;
        int generation = _dropGeneration;
        var deferral = e.GetDeferral();
        var point = e.GetPosition(_canvas);
        var (X, Y) = _geometry.ToNative(point.X, point.Y);
        uint operations = (uint)e.AllowedOperations & 3;
        try
        {
            if (await _session.QueryDropAsync(0) == 1)
            {
                if (_disposed || generation != _dropGeneration) return;
                await _session.QueryDropAsync(1, (int)X, (int)Y, TerminalDropData.Offer(e.DataView), operations);
                var accepted = (DataPackageOperation)await _session.QueryDropAsync(4, operations: operations);
                if (_disposed || generation != _dropGeneration) return;
                e.AcceptedOperation = _dropAccepted = accepted & e.AllowedOperations;
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text))
                e.AcceptedOperation = _dropAccepted = e.AllowedOperations & DataPackageOperation.Copy;
            e.Handled = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or OperationCanceledException or ArgumentException)
        { System.Diagnostics.Debug.WriteLine(ex); }
        finally { _dropHoverPending = false; deferral.Complete(); }
    }

    private void DragLeaveOnView(object sender, DragEventArgs e) => UiTasks.Observe(DragLeaveOnViewAsync(sender, e));
    private async Task DragLeaveOnViewAsync(object sender, DragEventArgs e)
    {
        _dropGeneration++; _dropAccepted = DataPackageOperation.None;
        try { if (_session is not null) await _session.QueryDropAsync(2); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void DropOnView(object sender, DragEventArgs e) => UiTasks.Observe(DropOnViewAsync(sender, e));
    private async Task DropOnViewAsync(object sender, DragEventArgs e)
    {
        _dropGeneration++; _dropAccepted = DataPackageOperation.None;
        var deferral = e.GetDeferral();
        var targetSession = _session;
        var point = e.GetPosition(_canvas);
        var (X, Y) = _geometry.ToNative(point.X, point.Y);
        uint operations = (uint)e.AllowedOperations & 3;
        e.AcceptedOperation = DataPackageOperation.None;
        try
        {
            var (representations, fallback) = await TerminalDropData.ReadAsync(e.DataView, _pane.Command);
            if (_disposed) return;
            if (targetSession is not null && await targetSession.QueryDropAsync(0) == 1)
            {
                int completed = await targetSession.DropAsync((int)X, (int)Y, representations, operations);
                e.AcceptedOperation = (DataPackageOperation)((uint)completed & operations);
            }
            else if (fallback is not null && (operations & 1) != 0 && targetSession is not null)
            { targetSession.Paste(fallback); e.AcceptedOperation = DataPackageOperation.Copy; }
            e.Handled = true;
            if (!_disposed) FocusTerminal();
            ValidationObserved?.Invoke("native-drop", e.AcceptedOperation.ToString());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or OperationCanceledException or ArgumentException)
        { System.Diagnostics.Debug.WriteLine(ex); }
        finally { deferral.Complete(); }
    }
}
