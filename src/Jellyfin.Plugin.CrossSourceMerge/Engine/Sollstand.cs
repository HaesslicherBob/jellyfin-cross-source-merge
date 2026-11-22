using System.Text.Json;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// What the last run created: per category the folder names it carries and
/// the planned files. The "cleanup" tab compares the target tree against it -
/// without reading the sources again.
/// </summary>
public sealed class Sollstand
{
    /// <summary>Time of the run (UTC, ISO 8601).</summary>
    public string Stand { get; set; } = string.Empty;

    /// <summary>Target path of the category to its expected list.</summary>
    public Dictionary<string, Sollkategorie> Kategorien { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads the expected state; if it is missing, an empty one is returned.</summary>
    public static Sollstand Laden(string datei)
    {
        try
        {
            if (File.Exists(datei))
            {
                var stand = JsonSerializer.Deserialize<Sollstand>(File.ReadAllText(datei));
                if (stand is not null)
                {
                    stand.Kategorien ??= new Dictionary<string, Sollkategorie>(StringComparer.Ordinal);
                    return stand;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Sollstand();
    }

    /// <summary>Writes the expected state.</summary>
    public void Speichern(string datei)
    {
        var ordner = Path.GetDirectoryName(datei);
        if (!string.IsNullOrEmpty(ordner))
        {
            Directory.CreateDirectory(ordner);
        }

        File.WriteAllText(datei, JsonSerializer.Serialize(this));
    }
}

/// <summary>Expected list of a category.</summary>
public sealed class Sollkategorie
{
    /// <summary>Folder names that carry a title (including shadow copies).</summary>
    public List<string> Ordner { get; set; } = new();

    /// <summary>Full paths of the planned files.</summary>
    public List<string> Dateien { get; set; } = new();
}
