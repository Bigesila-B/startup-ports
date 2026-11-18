using System.IO;
using System.Security;
using Microsoft.Win32;

namespace StartupPorts;

internal static class UpdatePreferences
{
    private const string StartupCheckValue = "CheckForUpdatesAtStartup";

    public static bool CheckAtStartup
    {
        get
        {
            try { return Registry.GetValue(AppIdentity.PreferencesKey, StartupCheckValue, 1) is not 0; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { return false; }
        }
        set => Registry.SetValue(AppIdentity.PreferencesKey, StartupCheckValue, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
