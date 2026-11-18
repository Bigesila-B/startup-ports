using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace StartupPorts;

internal static class UiText
{
    internal const string Chinese = "zh-CN";
    internal const string English = "en-US";
    private const string Preferences = @"HKEY_CURRENT_USER\Software\StartupPorts";
    private const string LanguageValue = "Language";
    private static readonly ResourceManager Strings = new("StartupPorts.Strings", typeof(UiText).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(Chinese);
    private static ResourceDictionary? _resources;

    public static string LanguageCode => Volatile.Read(ref _culture).Name;

    public static string L(string key) => Lookup(key, Volatile.Read(ref _culture));

    public static string L(string key, params object?[] args)
    {
        var culture = Volatile.Read(ref _culture);
        return string.Format(culture, Lookup(key, culture), args);
    }

    private static string Lookup(string key, CultureInfo culture) => Strings.GetString(key, culture)
        ?? throw new InvalidOperationException($"Missing UI resource {key}.");

    public static void LoadPreference()
    {
        string? saved = null;
        try { saved = Registry.GetValue(Preferences, LanguageValue, null) as string; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        SetLanguage(ResolveLanguage(saved, CultureInfo.CurrentUICulture));
    }

    internal static string ResolveLanguage(string? saved, CultureInfo systemLanguage) =>
        saved is Chinese or English ? saved :
        systemLanguage.TwoLetterISOLanguageName == "zh" ? Chinese : English;

    public static void SetLanguage(string code) => Volatile.Write(ref _culture, CultureInfo.GetCultureInfo(code));

    public static void SavePreference() => Registry.SetValue(Preferences, LanguageValue, LanguageCode, RegistryValueKind.String);

    public static void ApplyResources(Application app)
    {
        var localized = new ResourceDictionary();
        var culture = Volatile.Read(ref _culture);
        foreach (DictionaryEntry entry in Strings.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
        {
            var key = (string)entry.Key;
            localized[key] = Lookup(key, culture);
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var index = _resources is null ? -1 : dictionaries.IndexOf(_resources);
        if (index >= 0)
            dictionaries[index] = localized;
        else
            dictionaries.Add(localized);
        _resources = localized;
    }
}
