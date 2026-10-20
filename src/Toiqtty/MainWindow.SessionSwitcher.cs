using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toiqtty.Models;
using Windows.System;

namespace Toiqtty;

public sealed partial class MainWindow
{
    private sealed record SessionChoice(Workspace Session, string Label) { public override string ToString() => Label; }
    private async Task SwitchSession()
    {
        if (_dialogOpen || _saved.Workspaces.Count == 0) return;
        var search = new TextBox { PlaceholderText = "Find a session by name, directory or branch…", MaxLength = 256, MinWidth = 420 };
        var list = new ListView { IsItemClickEnabled = true, MaxHeight = 400 };
        var empty = new TextBlock { Text = "No matching sessions", Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 10 }; panel.Children.Add(search); panel.Children.Add(list); panel.Children.Add(empty);
        var dialog = new ContentDialog { Title = "Switch session", Content = panel, CloseButtonText = "Cancel" };
        Workspace? selected = null;
        void Filter()
        {
            list.Items.Clear();
            foreach (var entry in _saved.Workspaces.Select((session, order) => {
                string directory = session.AllPanes().FirstOrDefault(p => !p.IsBrowser)?.Session?.Directory ?? "";
                int nameScore = FuzzyMatch.Score(search.Text, session.Name);
                int metadataScore = FuzzyMatch.Score(search.Text, directory + " " + session.Branch);
                return (Session: session, Directory: directory, Score: Math.Max(nameScore < 0 ? -1 : nameScore + 100, metadataScore), Order: order);
            }).Where(e => e.Score >= 0).OrderByDescending(e => e.Score).ThenByDescending(e => e.Session == _selected).ThenBy(e => e.Order))
                list.Items.Add(new SessionChoice(entry.Session, entry.Session.Name + (entry.Session == _selected ? "  • current" : "") + "\n" + entry.Directory));
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
