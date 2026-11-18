using System.Runtime.InteropServices;
using System.Windows;

namespace StartupPorts;

internal static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            UiText.LoadPreference();
            if (args is ["--run", var id] && Guid.TryParseExact(id, "N", out var entry))
                return LaunchRunner.Run(entry);
            if (args is ["--prepare-upgrade"] or ["--uninstall"])
                return LaunchRunner.PrepareMaintenance(uninstall: args[0] == "--uninstall");
            if (args.Length > 0)
                return 2;

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) });
            UiText.ApplyResources(app);
            return app.Run(new MainWindow());
        }
        catch (Exception ex) when (args.Length > 0 && ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 1;
        }
    }
}
