using System.Globalization;
using System.IO;

namespace ScopeDesk;

internal static class DisplayPreferences
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopeDesk");
    private static readonly string FilePath = Path.Combine(Folder, "horizontal-divisions.txt");
    private static readonly string LanguageFile = Path.Combine(Folder, "language.txt");

    public static int LoadHorizontalDivisions()
    {
        if (int.TryParse(Read(FilePath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
            CaptureSettings.HorizontalDivisionOptions.Contains(value))
            return value;
        return CaptureSettings.DefaultHorizontalDivisions;
    }

    public static string LoadLanguage() => Read(LanguageFile) == "ru" ? "ru" : "en";

    public static void SaveLanguage(string language) => Write(LanguageFile, language);

    public static void SaveHorizontalDivisions(int value) => Write(FilePath, value.ToString(CultureInfo.InvariantCulture));

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Write(string path, string text)
    {
        string temporary = Path.Combine(Folder, Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { DeleteTemporary(temporary); }
        catch (UnauthorizedAccessException) { DeleteTemporary(temporary); }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
