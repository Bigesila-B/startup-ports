using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

namespace StartupPorts;

public partial class MainWindow
{
    private readonly StartupGroups _groups = new();
    private readonly StartupIcons _startupIcons = new();
    private IReadOnlyList<StartupGroupCard> _groupCards = [];
    private IReadOnlyList<StartupRow> _startupRows = [];
    private IReadOnlyList<StartupRow> _groupRows = [];
    private bool _syncingGroups;
    private bool _groupsAvailable;

    private StartupGroup? SelectedGroup => (GroupsList.SelectedItem as StartupGroupCard)?.Group;

    private IReadOnlyList<StartupGroupCard>? CaptureGroups(IReadOnlyList<StartupRow> rows, List<string> issues)
    {
        try
        {
            var entries = rows.ToDictionary(row => row.Entry.Id);
            return _groups.Read().Select(group => new StartupGroupCard(group, group.Members.Distinct()
                .Where(entries.ContainsKey).Select(id =>
                {
                    var row = entries[id];
                    return new GroupMember(row.Name, row.Icon);
                }).ToArray())).ToArray();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            issues.Add(L("GroupsReadError", ex.Message));
            return null;
        }
    }

    private BitmapSource? ReadStartupIcon(StartupEntry entry) => _startupIcons.FromExecutable(entry.Kind switch
    {
        ".ps1" => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ".bat" or ".cmd" => "cmd.exe",
        _ => entry.Target,
    });

    internal void UpdateStartupGroups(IReadOnlyList<StartupRow>? rows, IReadOnlyList<StartupGroupCard>? cards)
    {
        _groupsAvailable = cards != null;
        if (rows != null)
        {
            _startupRows = rows;
            var id = (StartupGrid.SelectedItem as StartupRow)?.Entry.Id;
            Replace(StartupGrid, rows, rows.FirstOrDefault(row => row.Entry.Id == id));
            StartupEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        TaskCount.Text = rows == null ? L("TasksUnavailable") :
            L("TaskCount", rows.Count, rows.Count(row => row.Entry.Enabled));

        if (cards != null && (cards.Count != _groupCards.Count || !cards.Zip(_groupCards).All(pair => pair.First.Matches(pair.Second))))
        {
            var selected = SelectedGroup?.Id;
            _groupCards = cards;
            _syncingGroups = true;
            try
            {
                GroupsList.ItemsSource = cards;
                GroupsList.SelectedItem = cards.FirstOrDefault(card => card.Group.Id == selected) ?? cards.FirstOrDefault();
            }
            finally { _syncingGroups = false; }
        }
        GroupsEmpty.Visibility = _groupsAvailable && _groupCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowGroupDetails();
    }

    private void ShowGroupDetails()
    {
        var group = SelectedGroup;
        var members = group?.Members.ToHashSet();
        var rows = members == null ? [] : _startupRows.Where(row => members.Contains(row.Entry.Id)).ToArray();
        if (!rows.Select(row => row.Entry).SequenceEqual(_groupRows.Select(row => row.Entry)))
            GroupMembersList.ItemsSource = rows;
        _groupRows = rows;
        GroupDetails.Visibility = group != null ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(GroupCollection, group == null ? 3 : 1);
        GroupMembersEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupTitle.Text = group?.Name ?? "";
        GroupSummary.Text = L("GroupSummary", rows.Length, rows.Count(row => row.Entry.Enabled));
        UpdateGroupControls();
    }

    private void UpdateGroupControls()
    {
        var selected = SelectedGroup != null;
        var canEdit = !_working && _groupsAvailable;
        GroupsList.IsEnabled = !_working;
        NewGroupButton.IsEnabled = canEdit;
        EditGroupButton.IsEnabled = DeleteGroupButton.IsEnabled = AddGroupItemButton.IsEnabled = canEdit && selected;
        RunGroupButton.IsEnabled = canEdit && selected && _groupRows.Any(CanRunGroupMember);
        EnableGroupButton.IsEnabled = canEdit && selected && _groupRows.Any(row => !row.Entry.Enabled);
        DisableGroupButton.IsEnabled = canEdit && selected && _groupRows.Any(row => row.Entry.Enabled);
    }

    private static bool CanRunGroupMember(StartupRow row) => row.Tracking == RunTracking.None && row.Task.State is not (2 or 4);

    private void GroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingGroups)
            ShowGroupDetails();
    }

    private async Task EditGroupAsync(StartupGroup? existing)
    {
        var dialog = new GroupDialog(existing, _startupRows) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Group is not { } group)
            return;
        await Change(() => _groups.Save(group, create: existing == null));
        if (!_closed && _groupCards.FirstOrDefault(card => card.Group.Id == group.Id) is { } card)
        {
            GroupsList.SelectedItem = card;
            GroupsList.ScrollIntoView(card);
        }
    }

    private async void NewGroupClicked(object sender, RoutedEventArgs e) => await EditGroupAsync(null);

    private async void EditGroupClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group)
            await EditGroupAsync(group);
    }

    private async void DeleteGroupClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group && Confirm(L("DeleteGroupPrompt", group.Name)))
            await Change(() => _groups.Delete(group.Id));
    }

    private async void AddGroupItemClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } group)
            await Edit(group: group);
    }

    private async Task ChangeGroupMembersAsync(bool? enable)
    {
        if (_working || !_groupsAvailable || SelectedGroup == null)
            return;
        var members = _groupRows;
        IReadOnlyList<string> errors = [];
        await Change(() => errors = ApplyGroupAction(new TaskStore(), members, enable));
        if (!_closed && errors.Count > 0)
            ShowError(new InvalidOperationException(L("GroupActionIncomplete", string.Join(Environment.NewLine, errors))));
    }

    internal static IReadOnlyList<string> ApplyGroupAction(TaskStore store, IReadOnlyList<StartupRow> members, bool? enable)
    {
        var errors = new List<string>();
        foreach (var row in members)
        {
            try
            {
                if (enable is { } enabled)
                {
                    if (store.Get(row.Entry.Id).Entry.Enabled != enabled)
                        store.SetEnabled(row.Entry.Id, enabled);
                }
                else if (CanRunGroupMember(row))
                    store.Run(row.Entry.Id);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                errors.Add($"{row.Name} · {ex.Message}");
            }
        }
        return errors;
    }

    private async void RunGroupClicked(object sender, RoutedEventArgs e) => await ChangeGroupMembersAsync(null);
    private async void EnableGroupClicked(object sender, RoutedEventArgs e) => await ChangeGroupMembersAsync(true);
    private async void DisableGroupClicked(object sender, RoutedEventArgs e) => await ChangeGroupMembersAsync(false);
}
