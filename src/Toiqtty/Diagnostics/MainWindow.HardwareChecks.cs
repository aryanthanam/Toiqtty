using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toiqtty.Models;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, ComboBox> _hardwareResults = new();
    private readonly List<object> _hardwareEvents = [];
    private readonly DispatcherTimer _hardwareTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Workspace? _hardwareWorkspace;
    private string? _hardwarePane;
    private TextBlock? _hardwareSummary;
    private DateTimeOffset _hardwareStarted;
    private int _hardwareResumeBaseline;
    private long _hardwareFrames;
    private int _hardwareResources;
    private bool _hardwareToastVerified;

    private async Task PrepareHardwareChecks()
    {
        try
        {
            _hardwareStarted = DateTimeOffset.UtcNow; _hardwareResumeBaseline = _resumeCount;
            string client = Environment.GetEnvironmentVariable("TOIQTTY_SMOKE_EXE") ?? throw new InvalidOperationException("Hardware input receiver is required.");
            string directory = SessionStore.Folder;
            File.WriteAllText(Path.Combine(directory, "drag fixture café.txt"), "Toiqtty disposable drag/drop fixture. Copy into the input receiver; do not move or delete other files.\n");
            NewWorkspace($"\"{client}\" --hardware-input \"{directory}\"", "Hardware checks");
            _hardwareWorkspace = _selected; _hardwarePane = _selected!.Root.Leaves().Single().Id;
            var hardwareWorkspace = _hardwareWorkspace!;
            string hardwarePane = _hardwarePane;
            _views.Single().ValidationObserved += (kind, text) => RecordHardwareEvent(kind, text);
            Title = "Toiqtty hardware checks — isolated";
            _metadataTimer.Stop();

            var panel = new StackPanel { Spacing = 7, Padding = new Thickness(14), Width = 370 };
            panel.Children.Add(new TextBlock { Text = "Physical validation", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "This window uses disposable sessions. Test input is recorded locally and never executed. Mark Pass only after performing the check.", TextWrapping = TextWrapping.Wrap });
            foreach (var (id, instruction) in new[]
            {
                ("ime", "IME: select a real candidate, commit 日本語, then Enter. Also cancel a candidate; canceled text must not appear below."),
                ("narrator", "Narrator: start it yourself, focus the terminal, and read/navigate the sample output. Confirm speech and focus are usable."),
                ("externalDrop", "File drop: drag ‘drag fixture café.txt’ from Explorer into the terminal, then Enter. Check its quoted path and that the source file remains."),
                ("toastClick", "Toast: click Send test toast, then click the Windows toast. The app should return to this pane; activation is recorded automatically."),
                ("sleepWake", "Sleep/wake: sleep this PC yourself, wake it, and check the red image, text and typing. A power event alone does not prove recovery."),
                ("gpuRecovery", "GPU recovery: if testing a real driver reset, perform it yourself and check image, text and typing afterward. Software device replacement is a separate automated check."),
                ("mixedDpi", "Mixed DPI: move this window between displays with different scaling. Check image, text, cursor and input position on both.")
            })
            {
                panel.Children.Add(new TextBlock { Text = instruction, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
                var result = new ComboBox { ItemsSource = new[] { "Pending", "Pass", "Fail", "Blocked" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(result, id + " physical check result");
                _hardwareResults[id] = result;
                result.SelectionChanged += (_, _) => SaveHardwareReport(); panel.Children.Add(result);
            }
            var sendToast = new Button { Content = "Send test toast" };
            sendToast.Click += async (_, _) =>
            {
                // Switch away so Windows activation must restore the actual target.
                NewWorkspace("cmd.exe /Q /K", "Toast background");
                await Task.Delay(200);
                bool sent = _notifications?.Show(hardwareWorkspace.Id, hardwarePane, "Hardware checks",
                    new(DateTimeOffset.Now, "Toiqtty physical notification check", "Click to restore the Hardware checks workspace and input pane.")) == true;
                RecordHardwareEvent("toast-sent", sent.ToString());
                if (!sent) SetStatusMessage(_notifications?.UnavailableReason ?? "Toast rate limit: wait two seconds and retry.", problem: true);
            };
            panel.Children.Add(sendToast);
            var restore = new Button { Content = "Return to input pane" };
            restore.Click += (_, _) => SelectWorkspace(hardwareWorkspace); panel.Children.Add(restore);
            var fixtureFolder = new Button { Content = "Open fixture folder" };
            fixtureFolder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                { Arguments = "\"" + directory + "\"", UseShellExecute = true });
            panel.Children.Add(fixtureFolder);
            var save = new Button { Content = "Save observations" };
            save.Click += (_, _) => { SaveHardwareReport(); SetStatusMessage("Hardware observations saved."); };
            panel.Children.Add(save);
            _hardwareSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            panel.Children.Add(_hardwareSummary);
            var overlay = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 28, 34)), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DimGray), BorderThickness = new Thickness(1, 0, 0, 0),
                Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, HorizontalAlignment = HorizontalAlignment.Right };
            // Reserve terminal space so the guide never covers the input surface.
            WorkspaceBody.Margin = new Thickness(0, 0, 370, 0); Grid.SetRow(overlay, 2); Root.Children.Add(overlay);
            var displays = Microsoft.UI.Windowing.DisplayArea.FindAll();
            if (displays.Count < 2) _hardwareResults["mixedDpi"].SelectedItem = "Blocked";
            Root.XamlRoot.Changed += (_, _) => RecordHardwareEvent("dpi-or-root-change", Root.XamlRoot.RasterizationScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _hardwareTimer.Tick += (_, _) => SaveHardwareReport(); _hardwareTimer.Start();
            Closed += (_, _) => { _hardwareTimer.Stop(); SaveHardwareReport(); };
            await Task.Delay(700); SaveHardwareReport();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(SessionStore.Folder, "hardware-startup-error.txt"), ex.ToString());
            SetStatusMessage("Hardware check startup failed: " + ex.Message, problem: true);
        }
    }

    internal async void RecordHardwareNotification(string workspace, string pane)
    {
        if (_hardwareWorkspace is null) return;
        RecordHardwareEvent("notification-callback", workspace + "/" + pane);
        await Task.Delay(200);
        if (_selected == _hardwareWorkspace && _focused?.PaneId == _hardwarePane && workspace == _hardwareWorkspace.Id && pane == _hardwarePane)
        { _hardwareToastVerified = true; _hardwareResults["toastClick"].SelectedItem = "Pass"; RecordHardwareEvent("notification-target-restored", "true"); }
    }

    private void RecordHardwareEvent(string kind, string text)
    {
        if (_hardwareEvents.Count >= 300) _hardwareEvents.RemoveAt(0);
        _hardwareEvents.Add(new { time = DateTimeOffset.UtcNow, kind, text = text.Length <= 1024 ? text : text[..1024] });
        SaveHardwareReport();
    }

    private void SaveHardwareReport()
    {
        if (_hardwareWorkspace is null || _hardwareResults.Count != 7) return;
        try
        {
            var results = _hardwareResults.ToDictionary(pair => pair.Key, pair => pair.Value.SelectedItem?.ToString() ?? "Pending");
            _hardwareFrames = Math.Max(_hardwareFrames, _views.Sum(view => view.FramesPainted));
            _hardwareResources = Math.Max(_hardwareResources, _views.Sum(view => view.ResourceGenerations));
            var report = new
            {
                version = BuildInfo.Version, diagnosticBuild = true, startedUtc = _hardwareStarted, updatedUtc = DateTimeOffset.UtcNow,
                completed = results.Values.All(value => value == "Pass"),
                toastActivationCallbackVerified = _hardwareToastVerified,
                results, events = _hardwareEvents, observedResumeCount = _resumeCount - _hardwareResumeBaseline,
                displayCount = Microsoft.UI.Windowing.DisplayArea.FindAll().Count,
                rasterizationScale = Root.XamlRoot?.RasterizationScale,
                framesPainted = _hardwareFrames, resourceGenerations = _hardwareResources,
                notes = "Pass/Fail/Blocked entries are user observations. toastActivationCallbackVerified requires the real activation callback and target focus. Power messages and device recreation alone do not establish physical sleep/reset. No system sleep, GPU reset, language installation or settings changes are initiated by this driver."
            };
            string path = Path.Combine(SessionStore.Folder, "hardware-check.json");
            string temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true);
            if (_hardwareSummary is not null) _hardwareSummary.Text = $"{results.Values.Count(value => value == "Pass")}/7 passed. Report: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
