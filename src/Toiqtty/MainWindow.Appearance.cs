using Microsoft.UI.Xaml.Controls;
using Toiqtty.Controls;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record WallpaperChoice(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }

    private WallpaperImage? _wallpaperImage;
    private string _wallpaperPath="",_wallpaperSignature="";
    private int _wallpaperIndex;
    private bool _wallpaperFocus;
    private void ApplyAppearanceSettings()
    {
        foreach(var workspace in _saved.Workspaces)
        {
            if(workspace.Theme is { } theme && _saved.Settings.ColorSchemes.Values.FirstOrDefault(t=>t.Name.Equals(theme.Name,StringComparison.OrdinalIgnoreCase)) is { } configured) workspace.Theme=configured;
            foreach(var pane in workspace.AllPanes())
            {
                if(_terminalViews.TryGetValue(pane.Id,out var view)) view.View.SetSessionTheme(ThemeFor(workspace));
                if(_browsers.TryGetValue(pane.Id,out var browser)) browser.SetSessionTheme(ThemeFor(workspace));
            }
        }
        _appliedSessionPalette=null;
        ApplyWallpaperSettings(); RefreshSidebar(); RefreshTabs(); UpdateChrome();
    }
    private void ApplyWallpaperSettings()
    {
        var settings=_saved.Settings.Wallpaper;
        string signature=string.Join('\0',settings.Images);
        if(_wallpaperSignature!=signature)
        {
            _wallpaperSignature=signature; _wallpaperIndex=settings.Random && settings.Images.Length>0?Random.Shared.Next(settings.Images.Length):0;
        }
        string path=settings.Enabled && !_wallpaperFocus && settings.Images.Length>0?settings.Images[Math.Clamp(_wallpaperIndex,0,settings.Images.Length-1)]:"";
        if(_wallpaperPath!=path)
        {
            _wallpaperPath=path; _wallpaperImage?.Dispose(); _wallpaperImage=null;
            if(path.Length>0)
            {
                var image=new WallpaperImage(path); _wallpaperImage=image;
                image.Ready+=()=> { if(!_closed && ReferenceEquals(image,_wallpaperImage)) foreach(var view in _terminalViews.Values) view.View.RefreshWallpaper(); };
                image.Failed+=message=> { if(!_closed && ReferenceEquals(image,_wallpaperImage)) SetStatusMessage(message,problem:true); };
                _=image.Load();
            }
        }
        foreach(var view in _terminalViews.Values) view.View.SetWallpaper(_wallpaperImage,settings);
    }
    private void CycleWallpaper(int direction,bool random=false)
    {
        int count=_saved.Settings.Wallpaper.Images.Length;
        if(count==0) { SetStatusMessage("Configure wallpaper.images or wallpaper.directory in Lua first.",problem:true); return; }
        _wallpaperFocus=false;
        _wallpaperIndex=random && count>1?(_wallpaperIndex+1+Random.Shared.Next(count-1))%count:(_wallpaperIndex+direction+count)%count;
        ApplyWallpaperSettings();
    }
    private async Task ChooseWallpaper()
    {
        if(_dialogOpen) return;
        if(_saved.Settings.Wallpaper.Images.Length==0) { SetStatusMessage("Configure wallpaper.images or wallpaper.directory in Lua first.",problem:true); return; }
        var list=new ListView { MaxHeight=400,IsItemClickEnabled=true };
        foreach(var path in _saved.Settings.Wallpaper.Images) list.Items.Add(new WallpaperChoice(path));
        var dialog=new ContentDialog { Title="Wallpaper",Content=list,CloseButtonText="Cancel" };
        string? selectedPath=null;
        list.ItemClick+=(_,args)=> { if(args.ClickedItem is WallpaperChoice choice) { selectedPath=choice.Path; dialog.Hide(); } };
        await ShowDialog(dialog);
        // Resolve against the current config: it may have reloaded while the chooser was open.
        int index=selectedPath is null?-1:Array.IndexOf(_saved.Settings.Wallpaper.Images,selectedPath);
        if(index<0) return;
        _wallpaperIndex=index; _wallpaperFocus=false; ApplyWallpaperSettings();
    }
    private void ToggleWallpaper() { _wallpaperFocus=!_wallpaperFocus; ApplyWallpaperSettings(); }
}
