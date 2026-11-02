using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Models;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record SessionChoice(Workspace Session, string Name, string Metadata, string Label, int Order, bool Current)
    {
        public override string ToString() => Label;
        internal int Score(string query)
        {
            int nameScore = FuzzyMatch.Score(query, Name);
            return Math.Max(nameScore < 0 ? -1 : nameScore + 100, FuzzyMatch.Score(query, Metadata));
        }
    }
    private async Task SwitchSession()
    {
        if (_dialogOpen || _saved.Workspaces.Count == 0) return;
        var search = new TextBox { PlaceholderText = "Find a session by name, directory or branch…", MaxLength = 256, MinWidth = 420 };
        var list = new ListView { IsItemClickEnabled = true, MaxHeight = 400 };
        var empty = new TextBlock { Text = "No matching sessions", Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(empty);
        var dialog = new ContentDialog { Title = "Switch session", Content = panel, CloseButtonText = "Cancel" };
        // Metadata is already cached by the sessions. Read it and walk pane trees once
        // per opening, rather than rebuilding every result object on every keystroke.
        var choices = _saved.Workspaces.Select((session, order) => {
            var pane = SidebarPane(session);
            string directory = pane?.Session?.Directory ?? pane?.Directory ?? "";
            string metadata = string.Join("  •  ", new[] { directory, session.Branch }.Where(value => !string.IsNullOrEmpty(value)));
            bool current = session == _selected;
            string label = session.Name + (current ? "  • current" : "") + (metadata.Length > 0 ? "\n" + metadata : "");
            return new SessionChoice(session, session.Name, metadata, label, order, current);
        }).ToArray();
        SessionChoice[]? matches = null;
        Workspace? selected = null;
        void Filter()
        {
            var filtered = choices.Select(choice => (Choice: choice, Score: choice.Score(search.Text)))
                .Where(entry => entry.Score >= 0).OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Choice.Current).ThenBy(entry => entry.Choice.Order)
                .Select(entry => entry.Choice).ToArray();
            if (matches is not null && matches.SequenceEqual(filtered)) return;
            // One collection replacement avoids a separate layout/selection notification
            // per row, and stable choices let unchanged results retain their selection.
            matches = filtered;
            list.ItemsSource = matches;
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1; empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Choose(SessionChoice choice) { selected = choice.Session; dialog.Hide(); }
        list.ItemClick += (_, args) => { if (args.ClickedItem is SessionChoice choice) Choose(choice); };
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, args) => {
            if (args.Key == VirtualKey.Enter && list.SelectedItem is SessionChoice choice) { args.Handled = true; Choose(choice); }
            else if (args.Key is VirtualKey.Down or VirtualKey.Up)
            {
                args.Handled = true;
                if (list.Items.Count == 0) return;
                int index = Math.Clamp(list.SelectedIndex + (args.Key == VirtualKey.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.SelectedIndex = index; list.ScrollIntoView(list.Items[index]);
            }
        };
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        Filter(); await ShowDialog(dialog);
        if (selected is not null && _saved.Workspaces.Contains(selected)) SelectWorkspace(selected);
    }
}
