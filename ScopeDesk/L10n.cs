using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;

namespace ScopeDesk;

internal static class L10n
{
    private static readonly ResourceManager Strings = new("ScopeDesk.Strings", typeof(L10n).Assembly);
    private static CultureInfo culture = CultureInfo.GetCultureInfo("en-US");
    private static ResourceDictionary? labels;

    public static CultureInfo Culture => Volatile.Read(ref culture);
    public static string Language => Culture.TwoLetterISOLanguageName;
    public static string Text(string key) => Strings.GetString(key, Culture) ?? key;
    public static string Format(string key, params object[] values)
    {
        var current = Culture;
        return string.Format(current, Strings.GetString(key, current) ?? key, values);
    }

    public static void Apply(string language)
    {
        var next = CultureInfo.GetCultureInfo(language == "ru" ? "ru-RU" : "en-US");
        Volatile.Write(ref culture, next);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = next;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = next;

        var dictionary = new ResourceDictionary();
        foreach (DictionaryEntry entry in Strings.GetResourceSet(next, createIfNotExists: true, tryParents: true)!)
            dictionary.Add(entry.Key, entry.Value);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Add(dictionary);
        if (labels is not null)
            dictionaries.Remove(labels);
        labels = dictionary;
    }
}
