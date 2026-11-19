using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StartupPorts;

internal static class GroupChecks
{
    public static void CheckStorage()
    {
        var directory = Directory.CreateTempSubdirectory("StartupPorts group checks ");
        try
        {
            var path = Path.Combine(directory.FullName, "Groups.json");
            var store = new StartupGroups(path);
            Require(store.Read().Count == 0, "Missing group storage did not start empty.");
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var group = new StartupGroup(Guid.NewGuid(), "开发环境", [first]);
            store.Save(group, create: true);
            new StartupGroups(path).AddMember(group.Id, second);
            store.AddMember(group.Id, second);
            var read = store.Read().Single();
            Require(read.Id == group.Id && read.Name == group.Name && read.Members.SequenceEqual(new[] { first, second }),
                "The saved group or its unique membership did not survive reopening.");
            store.Save(read with { Name = "Local services", Members = [second] }, create: false);
            read = store.Read().Single();
            Require(read.Name == "Local services" && read.Members.SequenceEqual(new[] { second }), "Group editing failed.");
            store.Delete(group.Id);
            Require(store.Read().Count == 0, "Deleting a group left it in storage.");
            Console.WriteLine("Verified creating, reopening, editing, and deleting startup groups.");
        }
        finally { directory.Delete(recursive: true); }
    }

    public static void CheckWindow(MainWindow window, string executable, BitmapSource icon)
    {
        StartupRow Row(string name, bool enabled) => new(new(new(Guid.NewGuid(), name, executable, "",
            Path.GetDirectoryName(executable)!, 0, null, Protocol.Tcp, enabled), 3), null, new([], []), RunTracking.None);

        var app = Row("Explorer", true) with { Icon = icon };
        var script = Row("Example script", false);
        var other = Row("Other app", false);
        var group = new StartupGroup(Guid.NewGuid(), "开发工具", [app.Entry.Id, script.Entry.Id]);
        var card = new StartupGroupCard(group, [new(app.Name, icon), new(script.Name, null)]);
        var cards = (ListBox)window.FindName("GroupsList");
        var grid = (DataGrid)window.FindName("StartupGrid");
        var members = (ListBox)window.FindName("GroupMembersList");
        var startupPage = (FrameworkElement)window.FindName("StartupPanel");
        var groupsPage = (FrameworkElement)window.FindName("GroupsPanel");
        var addItem = (Button)window.FindName("AddButton");
        var addGroup = (Button)window.FindName("NewGroupButton");
        window.UpdateStartupGroups([app, script, other], [card]);
        Require(grid.Items.Count == 3 && cards.Items.Count == 1, "The startup group card was not presented.");
        Require(startupPage.Visibility == Visibility.Visible && groupsPage.Visibility == Visibility.Collapsed &&
            addItem.Visibility == Visibility.Visible && addGroup.Visibility == Visibility.Collapsed,
            "The single-item page exposed group actions.");
        ((RadioButton)window.FindName("GroupsNav")).IsChecked = true;
        cards.SelectedItem = card;
        Require(startupPage.Visibility == Visibility.Collapsed && groupsPage.Visibility == Visibility.Visible &&
            addItem.Visibility == Visibility.Collapsed && addGroup.Visibility == Visibility.Visible,
            "The group page exposed single-item actions.");
        Require(members.Items.Cast<StartupRow>().Select(row => row.Entry.Id).SequenceEqual(group.Members) && grid.Items.Count == 3,
            "Selecting a group changed the full startup list or showed the wrong members.");
        foreach (var name in new[] { "RunGroupButton", "EnableGroupButton", "DisableGroupButton" })
            Require(((Button)window.FindName(name)).IsEnabled, $"The mixed group could not use {name}.");
        window.UpdateStartupGroups([app, script, other], [new(group, card.Members)]);
        Require(cards.SelectedItem == card && members.Items.Count == 2, "Refreshing cleared the selected group.");
        Require(((TextBlock)window.FindName("GroupSummary")).Text == UiText.L("GroupSummary", 2, 1),
            "The group summary did not show its mixed sign-in state.");

        var presenter = new ContentPresenter { Content = card, ContentTemplate = cards.ItemTemplate };
        presenter.Measure(new Size(114, 100));
        presenter.Arrange(new Rect(0, 0, 114, 100));
        presenter.UpdateLayout();
        var visuals = Descendants(presenter).OfType<FrameworkElement>().ToArray();
        Require(visuals.OfType<Image>().Any(image => image.Source == icon && image.Visibility == Visibility.Visible && image.ActualWidth > 0) &&
            visuals.Any(element => element.Name == "ProcessFallback" && element.Visibility == Visibility.Visible && element.ActualWidth > 0),
            "The group card failed to show both an app thumbnail and a generic icon.");

        ((RadioButton)window.FindName("StartupNav")).IsChecked = true;
        Require(cards.SelectedItem == card && grid.Items.Count == 3 && startupPage.Visibility == Visibility.Visible,
            "The full startup list could not be restored without losing the selected group.");

        foreach (var language in new[] { "en-US", "zh-CN" })
        {
            window.ApplyLanguage(language);
            var dialog = new GroupDialog(group, [app, script, other]);
            try { Require(dialog.Title == UiText.L("EditGroup"), "The group editor title was not localized."); }
            finally { dialog.Close(); }
        }
        Console.WriteLine("Verified separate startup and group pages, member thumbnails, mixed sign-in status, and both editor languages.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
