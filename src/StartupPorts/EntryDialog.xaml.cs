using System.IO;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;

using static StartupPorts.UiText;

namespace StartupPorts;

public partial class EntryDialog : Window
{
    private readonly Guid _id;
    internal StartupEntry? Entry { get; private set; }

    internal EntryDialog(StartupEntry? existing, PortRow? port)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(LanguageCode);
        _id = existing?.Id ?? Guid.NewGuid();
        Title = Heading.Text = existing is null ? L("AddEntry") : L("EditEntry");
        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            TargetBox.Text = existing.Target;
            ArgumentsBox.Text = existing.Arguments;
            DirectoryBox.Text = existing.WorkingDirectory;
            DelayBox.Text = existing.DelaySeconds.ToString();
            PortBox.Text = existing.ExpectedPort?.ToString() ?? "";
            ProtocolBox.SelectedIndex = (int)existing.ExpectedProtocol;
            EnabledBox.IsChecked = existing.Enabled;
        }
        else if (port is not null)
        {
            ImportHint.Visibility = Visibility.Visible;
            NameBox.Text = Path.GetFileNameWithoutExtension(port.Process);
            TargetBox.Text = port.Path;
            PortBox.Text = port.Port.ToString();
            ProtocolBox.SelectedIndex = (int)port.Endpoint.Protocol;
            EnabledBox.IsChecked = false;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private void BrowseTarget(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = L("ExecutableFilter"),
        };
        if (picker.ShowDialog(this) != true)
            return;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(picker.FileName);
        TargetBox.Text = picker.FileName;
    }

    private void BrowseDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog();
        if (Directory.Exists(DirectoryBox.Text))
            picker.InitialDirectory = DirectoryBox.Text;
        if (picker.ShowDialog(this) == true)
            DirectoryBox.Text = picker.FolderName;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(DelayBox.Text, out var delay))
                throw new ArgumentException(L("DelayIntegerError"));
            int? port = null;
            if (!string.IsNullOrWhiteSpace(PortBox.Text))
            {
                if (!int.TryParse(PortBox.Text, out var value))
                    throw new ArgumentException(L("PortIntegerError"));
                port = value;
            }
            var target = TargetBox.Text.Trim();
            var directory = DirectoryBox.Text.Trim();
            if (directory.Length == 0)
                directory = Path.GetDirectoryName(target) ?? "";
            Entry = new StartupEntry(_id, NameBox.Text.Trim(), target, ArgumentsBox.Text,
                directory, delay, port, (Protocol)ProtocolBox.SelectedIndex, EnabledBox.IsChecked == true).Validate();
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
