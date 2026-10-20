using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private async Task RunDesignSmoke()
    {
        _metadataTimer.Stop();
        var checks=new List<string>();
        void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            var initial=_selected!;
            string shell=ShellDefaults.Command+" -NoProfile";
            _saved.Settings.Shell=shell;
            NewWorkspace(shell,"toiqtty"); RemoveWorkspace(initial);
            _selected!.ActiveTab.Name="Development";
            await Capture("single-terminal",1280);
            NewTab(shell, "Build & tests");
            SelectTab(_selected.Tabs[0]); await Task.Delay(100); Split(true);
            var project = _selected;
            NewWorkspace(shell, "Documentation");
            NewWorkspace(shell, "API server");
            _selected!.Attention = true;
            SelectWorkspace(project); project.Branch = "main";
            var ready=DateTime.UtcNow.AddSeconds(8);
            while (_views.Any(view=>!view.RenderedText.Contains("PS ")) && DateTime.UtcNow<ready) await Task.Delay(50);
            Check(_views.All(view=>view.RenderedText.Contains("PS ")),"Real PowerShell prompts render before preview commands");
            int index=0;
            foreach (var pane in project.Root.Leaves())
            {
                pane.Session!.Paste(index++==0?"Get-Location; Get-ChildItem src -Name":"dotnet --version; Get-Process -Id $PID | Select-Object ProcessName, Id"); pane.Session.Key(13, 0);
            }
            RefreshSidebar();
            await Task.Delay(800);
            Check(_views.Count==2,"Design fixture has two live split terminal panes");
            project.Root.Leaves().First().Attention=true; RefreshTabs();
            FocusView(_views.First());
            var activeTab=TabStrip.Items.OfType<ListViewItem>().First(b=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b)=="Tab-"+project.ActiveTab.Id);
            Check(!Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(activeTab).Contains("needs attention") && Microsoft.UI.Xaml.Automation.AutomationProperties.GetItemStatus(activeTab)=="Selected","Focusing a pane clears its tab attention indicator and announces selection");
            await RunExplorerChecks(Check);
            await RunStatusBarChecks(Check, Capture);
            await RunSessionThemeChecks(Check, Capture);
            await RunAppearanceChecks(Check,Capture);
            await RunInteractionUiChecks(Check);
            foreach (var (name, width) in new[] { ("wide", 1280), ("compact", 960), ("narrow", 760) })
            {
                await Capture(name,width);
                CheckUnifiedTitleBar(Check, name);
                Check(ShellButton.TransformToVisual(Root).TransformPoint(new()).X+ShellButton.ActualWidth<=Root.ActualWidth+1,"Toolbar actions remain inside the "+name+" window");
            }
            ShellMenu.ShowAt(ShellButton);
            var menuReady=DateTime.UtcNow.AddSeconds(2);
            while (SplitRightButton.ActualHeight==0 && DateTime.UtcNow<menuReady) await Task.Delay(50);
            Check(SplitRightButton.ActualHeight>0 && ClosePaneMenuItem.ActualHeight>0,"Split and close-pane actions remain reachable in the native overflow menu");
            ShellMenu.Hide();
            var sessions=project.AllPanes().Select(p=>p.Session).ToArray();
            ToggleSidebar(); await Task.Delay(100);
            Check(SidebarRestoreButton.Visibility==Visibility.Visible && SidebarRestoreButton.ActualWidth>0,"Hiding the sidebar leaves a visible restore control");
            await Capture("sidebar-hidden",960); CheckUnifiedTitleBar(Check, "sidebar hidden"); ToggleSidebar();
            Check(project.AllPanes().Select(p=>p.Session).SequenceEqual(sessions),"Changing chrome preserves running terminal sessions");
            var moving = MovePaneDialog(project.Root.Leaves().First().Id); await Task.Delay(250);
            var moveDialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p=>Descendants(p.Child)).OfType<ContentDialog>().FirstOrDefault();
            Check(moveDialog is not null,"Move pane opens a native destination dialog");
            if (moveDialog is not null)
            {
                var choices=Descendants(moveDialog).OfType<ComboBox>().ToArray();
                Check(choices.Length==2 && choices[0].Items.Count==project.Tabs.Count+1
                    && choices[0].Items.OfType<ComboBoxItem>().Where(i=>i.Tag is TerminalTab).All(i=>project.Tabs.Contains((TerminalTab)i.Tag)),
                    "Move pane offers only tabs in its owning session and a new-tab destination");
                var layoutBefore=System.Text.Json.JsonSerializer.Serialize(_saved);
                await Capture("move-pane",960,moveDialog); moveDialog.Hide(); await moving;
                Check(layoutBefore==System.Text.Json.JsonSerializer.Serialize(_saved) && project.AllPanes().Select(p=>p.Session).SequenceEqual(sessions),
                    "Cancelling a pane move preserves the layout and all live shells");
            }
            PaletteClick(this,new()); await Task.Delay(350);
            var dialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p=>Descendants(p.Child)).OfType<ContentDialog>().FirstOrDefault();
            Check(dialog is not null,"Command palette opens as a native dialog");
            if (dialog is not null)
            {
                Check(Descendants(dialog).OfType<ListViewItem>().Any(item=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(item).StartsWith("Open current folder in Windows Explorer")),
                    "The command palette exposes the current-folder Explorer action");
                await Capture("palette",960,dialog); dialog.Hide(); await WaitForDialogClose();
            }
            SettingsClick(this,new()); await Task.Delay(250);
            var settings=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p=>Descendants(p.Child)).OfType<ContentDialog>().FirstOrDefault();
            Check(settings is not null,"Settings opens with native grouped controls");
            if (settings is not null) { await Capture("settings",960,settings); settings.Hide(); await WaitForDialogClose(); }
            NewBrowser("about:blank"); await Task.Delay(800);
            await Capture("browser",960);
            SelectWorkspace(project);
            for(int i=0;i<8;i++) NewTab("cmd.exe /Q /K","Overflow tab "+i+" with a longer name");
            await Capture("tab-overflow",760);
            Check(TabOverflowButton.Visibility==Visibility.Visible && TitleHitTest(TabOverflowButton)==1,
                "Narrow windows expose an interactive all-tabs menu without another title row");
            TabOverflowOpening(TabOverflowMenu,new());
            Check(TabOverflowMenu.Items.Count==project.Tabs.Count && TabOverflowMenu.Items.OfType<ToggleMenuFlyoutItem>().Count(item=>item.IsChecked)==1,
                "The overflow menu lists every tab and identifies exactly the active tab");
            var firstTab=project.Tabs[0]; SelectTab(firstTab); await Task.Delay(100);
            Check(_tabCards[firstTab.Id].Close.Opacity==1 && _tabCards.Where(pair=>pair.Key!=firstTab.Id).All(pair=>pair.Value.Close.Opacity==0),
                "Active tabs expose their close button while inactive tabs keep the strip quiet until hover or keyboard focus");
            Check(SidebarColumn.Width.Value<=165,"The sidebar adapts to narrow windows to give more width to terminal panes");
            foreach (var workspace in _saved.Workspaces.ToArray()) RemoveWorkspace(workspace);
            await Capture("empty",960);
            Check(_saved.Workspaces.Count==0 && _views.Count==0,"Empty session state creates no replacement shells");
            Check(!OpenDirectoryButton.IsEnabled && ExplorerDirectory(null) is null,"Empty sessions disable the Explorer button and have no folder target");
            File.WriteAllText(Path.Combine(SessionStore.Folder, "design-smoke.json"),System.Text.Json.JsonSerializer.Serialize(new { passed=true,checks }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(SessionStore.Folder, "design-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); }
        finally { Close(); }

        async Task Capture(string name,int width,UIElement? element=null)
        {
            double scale=Root.XamlRoot.RasterizationScale;
            AppWindow.Resize(new((int)(width*scale),(int)(700*scale)));
            await Task.Delay(450);
            var bitmap=new RenderTargetBitmap(); await bitmap.RenderAsync(element??Root);
            var buffer=await bitmap.GetPixelsAsync(); byte[] pixels=new byte[buffer.Length];
            using (var reader=DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            // RenderTargetBitmap excludes swapchain surfaces. Capture the same
            // terminal renderer at its actual DPI and composite at its XAML origin.
            if(element is null)
            foreach(var view in _views.Where(view=>view.HasGraphicsDeviceForCheck))
            {
                using var terminal=view.CreateRenderTargetForCheck(); view.RenderFrameForCheck(terminal);
                var colors=terminal.GetPixelColors(); int widthPixels=(int)terminal.SizeInPixels.Width;
                var origin=view.ContentOriginForCheck(Root); int left=(int)Math.Round(origin.X*scale),top=(int)Math.Round(origin.Y*scale);
                for(int y=0;y<terminal.SizeInPixels.Height;y++)
                for(int x=0;x<widthPixels;x++)
                {
                    int px=left+x,py=top+y;
                    if(px<0 || py<0 || px>=bitmap.PixelWidth || py>=bitmap.PixelHeight) continue;
                    var color=colors[y*widthPixels+x]; int offset=(py*bitmap.PixelWidth+px)*4;
                    pixels[offset]=color.B; pixels[offset+1]=color.G; pixels[offset+2]=color.R; pixels[offset+3]=color.A;
                }
            }
            string path = Path.Combine(SessionStore.Folder, "design-" + name + ".png");
            File.WriteAllBytes(path, []);
            var file=await StorageFile.GetFileFromPathAsync(path);
            using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96*scale,96*scale,pixels);
            await encoder.FlushAsync();
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject? node)
        {
            if (node is null) yield break;
            yield return node;
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(node,i))) yield return child;
        }
        async Task WaitForDialogClose()
        {
            var deadline=DateTime.UtcNow.AddSeconds(3);
            while (_dialogOpen && DateTime.UtcNow<deadline) await Task.Delay(50);
            Check(!_dialogOpen,"Native dialog closes before the next action");
        }
    }
}
