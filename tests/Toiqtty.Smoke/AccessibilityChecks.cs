using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Text;

internal static class AccessibilityChecks
{
    // A separate process talks to the Windows UIA broker, exercising COM
    // marshalling and the actual exported tree instead of calling our peer.
    public static Task Run(int pid, string report) => Task.Run(() =>
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            var window = AutomationElement.FromHandle(process.MainWindowHandle);
            // Focus checks require the test's own window to be active. Another
            // desktop window can otherwise retain foreground keyboard focus.
            window.SetFocus();
            var terminals = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Toiqtty.Terminal"));
            Check(terminals.Count == 3, "external UIA client discovers each terminal document");
            foreach (AutomationElement terminal in terminals)
            {
                Check(terminal.TryGetCurrentPattern(TextPattern.Pattern, out var pattern), "terminal exports the native UIA Text pattern");
                var text = (TextPattern)pattern;
                var document = text.DocumentRange;
                Check(document.GetText(-1).Contains("TOIQTTY_UI_SMOKE"), "external screen-reader client receives terminal output");
                var match = document.FindText("TOIQTTY_UI_SMOKE", false, false);
                var rectangles = match.GetBoundingRectangles();
                Check(rectangles.Length > 0 && rectangles.All(r => r.Width > 0 && r.Height > 0), "external text ranges expose visible screen rectangles");
                Check(match.Clone().Compare(match), "text ranges clone across the COM boundary");
                Check(document.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true, "terminal text declares its read-only attribute");
                Check(ReferenceEquals(document.GetAttributeValue(TextPattern.FontNameAttribute), AutomationElement.NotSupported), "unsupported text attributes use UIA's reserved value");
                var character = match.Clone(); character.MoveEndpointByRange(TextPatternRangeEndpoint.End, character, TextPatternRangeEndpoint.Start);
                character.ExpandToEnclosingUnit(TextUnit.Character);
                Check(character.GetText(-1) == "O", "external ranges navigate by character");
                var accent = document.FindText("\u0301", false, false);
                Check(accent is not null && accent.GetBoundingRectangles().Length > 0, "a range inside a combining cluster still has visible cell geometry");
                accent!.Select();
                Check(text.GetSelection().Single().GetText(-1) == "e\u0301", "UIA selection normalizes a combining suffix to its complete engine cluster");
                match.Select();
                Check(text.GetSelection().Single().GetText(-1) == "TOIQTTY_UI_SMOKE", "external UIA selection changes the engine selection");
                Check(text.GetSelection().Single().Compare(match), "UIA range comparison stays stable after selection updates the document snapshot");
                var finalCharacter = document.Clone();
                finalCharacter.MoveEndpointByRange(TextPatternRangeEndpoint.Start, finalCharacter, TextPatternRangeEndpoint.End);
                finalCharacter.ExpandToEnclosingUnit(TextUnit.Character);
                Check(finalCharacter.Move(TextUnit.Character, 1) == 0, "nondegenerate UIA movement stops at the last unit without reporting a phantom move");
                document.FindText("TOIQTTY_UI_SMOKE", false, false).ScrollIntoView(true);
                terminal.SetFocus();
                var focused = AutomationElement.FocusedElement;
                bool HasTerminalText() => focused.Current.ProcessId == pid && focused.TryGetCurrentPattern(TextPattern.Pattern, out var focusedPattern)
                    && ((TextPattern)focusedPattern).DocumentRange.GetText(-1).Contains("TOIQTTY_UI_SMOKE");
                // The Windows broker can acknowledge SetFocus before the
                // foreground/focus update reaches the external UIA client.
                var focusWait = Stopwatch.StartNew();
                while (!HasTerminalText() && focusWait.Elapsed < TimeSpan.FromSeconds(2))
                { Thread.Sleep(20); focused = AutomationElement.FocusedElement; }
                Check(HasTerminalText(), "focused terminal input exposes output text to the screen reader; observed process=" + focused.Current.ProcessId + ", class=" + focused.Current.ClassName);
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, checks, error = ex.ToString() }));
            throw;
        }
    });
}
