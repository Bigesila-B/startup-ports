using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

namespace StartupPorts;

public partial class GroupDialog : Window
{
    private readonly Guid _id;
    private readonly MemberChoice[] _members;
    private readonly Guid[] _unlistedMembers;

    internal StartupGroup? Group { get; private set; }

    internal GroupDialog(StartupGroup? existing, IReadOnlyList<StartupRow> rows)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(LanguageCode);
        _id = existing?.Id ?? Guid.NewGuid();
        Title = Heading.Text = L(existing == null ? "NewGroup" : "EditGroup");
        NameBox.Text = existing?.Name ?? "";
        var memberIds = existing?.Members.ToHashSet() ?? [];
        _members = rows.Select(row => new MemberChoice(row, memberIds.Contains(row.Entry.Id))).ToArray();
        _unlistedMembers = memberIds.Except(rows.Select(row => row.Entry.Id)).ToArray();
        Choices.ItemsSource = _members;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = L("GroupNameRequired");
            return;
        }
        Group = new(_id, name, _members.Where(choice => choice.Selected).Select(choice => choice.Id)
            .Concat(_unlistedMembers).ToArray());
        DialogResult = true;
    }

    private sealed class MemberChoice(StartupRow row, bool selected)
    {
        public Guid Id => row.Entry.Id;
        public string Name => row.Name;
        public string Target => row.Entry.Target;
        public BitmapSource? Icon => row.Icon;
        public bool Selected { get; set; } = selected;
    }
}
