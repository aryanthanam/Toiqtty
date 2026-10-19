using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Oroterm.Controls;
using Oroterm.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task RunAppearanceChecks(Action<bool,string> check,Func<string,int,UIElement?,Task> capture)
    {
        var wallpaper=_saved.Settings.Wallpaper; ushort weight=_saved.Settings.FontWeight;
        var view=_views[0]; var shells=_selected!.AllPanes().Select(p=>p.Session).ToArray();
        async Task Wait(Func<bool> condition)
        {
            var until=DateTime.UtcNow.AddSeconds(8);
            while(!condition()) { if(DateTime.UtcNow>until) throw new TimeoutException("Appearance UI state did not settle."); await Task.Delay(30); }
        }
        Windows.UI.Color Sample()
        {
            using var target=view.CreateRenderTargetForCheck(); view.RenderFrameForCheck(target);
            return target.GetPixelColors()[^1];
        }
        try
        {
            _saved.Settings.FontWeight=500; foreach(var terminal in _views) terminal.ApplySettings();
            await Wait(()=>_views.All(v=>!v.ResizePendingForCheck));
            check(view.FontWeightForCheck==500 && view.FontFaceForCheck.Contains("Medium.ttf",StringComparison.Ordinal),
                "Win2D resolves the actual bundled Medium face when font_weight changes");
            string red=Path.Combine(SessionStore.Folder,"wallpaper-red.png"),blue=Path.Combine(SessionStore.Folder,"wallpaper-blue.png");
            await SaveImage(red,255,0,0); await SaveImage(blue,0,0,255);
            _saved.Settings.Wallpaper=new() { Images=[red,blue],Random=false,Opacity=1,Fit="cover" }; ApplyWallpaperSettings();
            await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            check(Sample()==Colors.Red,"A decoded wallpaper is composited behind default terminal cells");
            CycleWallpaper(1); await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            check(Sample()==Colors.Blue,"Cycling wallpapers changes rendered pixels without restarting shells");
            view.ReplaceRenderDeviceForCheck(true); await Wait(()=>view.HasGraphicsDeviceForCheck && view.FontWeightForCheck==500 && !view.ResizePendingForCheck && Sample()==Colors.Blue);
            check(_wallpaperImage!.DeviceCountForCheck<=2,"Wallpaper resources recreate on a replacement rendering device with a bounded cache");
            view.ReplaceRenderDeviceForCheck(false); await Wait(()=>view.HasGraphicsDeviceForCheck && !view.ResizePendingForCheck);
            ToggleWallpaper();
            check(_wallpaperImage is null && Sample()==Chrome.Brush(view.FrameForCheck.Background).Color,
                "Wallpaper focus mode restores the terminal's effective background");
            ToggleWallpaper(); await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            CycleWallpaper(1,true); await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            check(Sample()==Colors.Red,"Random wallpaper selection avoids repeating the current image when alternatives exist");
            _saved.Settings.Wallpaper.Opacity=.3; ApplyWallpaperSettings();
            await capture("wallpaper",1280,null);
            string duplicate=Path.Combine(SessionStore.Folder,"other","wallpaper-red.png");
            Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!); await SaveImage(duplicate,0,0,255);
            _saved.Settings.Wallpaper.Images=[red,blue,duplicate]; _saved.Settings.Wallpaper.Opacity=1; ApplyWallpaperSettings();
            await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            var choosing=ChooseWallpaper(); await Wait(()=>FindChooser() is not null);
            var dialog=FindChooser()!; var list=(ListView)dialog.Content;
            check(list.Items.Count==3 && list.Items[0].ToString()==list.Items[2].ToString(),
                "Wallpaper controls expose configured filenames, including duplicate names, in a native chooser");
            await InvokeChoice(list,2); await choosing.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            check(_wallpaperPath==duplicate && Sample()==Colors.Blue && !_dialogOpen && shells.SequenceEqual(_selected.AllPanes().Select(p=>p.Session)),
                "Invoking a real chooser row selects its exact file and renders it without closing any shell");
            var retained=_wallpaperImage;
            choosing=ChooseWallpaper(); await Wait(()=>FindChooser() is not null);
            FindChooser()!.Hide(); await choosing;
            check(ReferenceEquals(retained,_wallpaperImage) && _wallpaperPath==duplicate,
                "Cancelling the wallpaper chooser preserves the current image");
            choosing=ChooseWallpaper(); await Wait(()=>FindChooser() is not null);
            list=(ListView)FindChooser()!.Content;
            _saved.Settings.Wallpaper.Images=[red]; ApplyWallpaperSettings();
            await InvokeChoice(list,2); await choosing.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait(()=>_wallpaperImage?.ReadyForCheck==true);
            check(_wallpaperPath==red && Sample()==Colors.Red,
                "Choosing an image removed by a config reload safely leaves the current wallpaper unchanged");
            string invalid=Path.Combine(SessionStore.Folder,"invalid-wallpaper.png"); File.WriteAllText(invalid,"invalid image");
            _saved.Settings.Wallpaper.Images=[invalid]; ApplyWallpaperSettings();
            await Wait(()=>StatusNotice.IsOpen);
            check(StatusNotice.Message.Contains("wallpaper",StringComparison.OrdinalIgnoreCase) && shells.SequenceEqual(_selected.AllPanes().Select(p=>p.Session)),
                "Invalid wallpaper data reports a dismissible error while preserving every live shell");
        }
        finally
        {
            StatusNotice.IsOpen=false; view.ReplaceRenderDeviceForCheck(false);
            _saved.Settings.FontWeight=weight; _saved.Settings.Wallpaper=wallpaper; _wallpaperFocus=false; ApplyWallpaperSettings();
            foreach(var terminal in _views) terminal.ApplySettings();
        }
        ContentDialog? FindChooser() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .SelectMany(p=>Walk(p.Child)).OfType<ContentDialog>().FirstOrDefault();
        async Task InvokeChoice(ListView list,int index)
        {
            list.UpdateLayout();
            await Wait(()=>list.ContainerFromIndex(index) is ListViewItem);
            var item=(ListViewItem)list.ContainerFromIndex(index);
            check(Walk(item).OfType<TextBlock>().Any(text=>text.Text==list.Items[index].ToString()),
                "Native wallpaper chooser rows display readable filenames");
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement(item)!;
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
        static async Task SaveImage(string path,byte red,byte green,byte blue)
        {
            File.WriteAllBytes(path,[]); var file=await StorageFile.GetFileFromPathAsync(path); using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,1,1,96,96,[blue,green,red,255]); await encoder.FlushAsync();
        }
        static IEnumerable<DependencyObject> Walk(DependencyObject node)
        {
            yield return node;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) foreach(var child in Walk(VisualTreeHelper.GetChild(node,i))) yield return child;
        }
    }
}
