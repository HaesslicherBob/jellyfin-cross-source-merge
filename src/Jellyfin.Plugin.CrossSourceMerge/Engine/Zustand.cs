using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>What a run remembers over time.</summary>
public sealed class Zustand
{
    private static readonly JsonSerializerOptions Optionen = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Size/duration/resolution per title ('art|key').</summary>
    public Dictionary<string, Manifestwert> Manifest { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Display name chosen once per title ('art|key').</summary>
    public Dictionary<string, string> Namen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Manual mapping per source folder.</summary>
    public Dictionary<string, Zuordnung> Zuordnungen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Categories per title ('art|key' or 'tag|folder').</summary>
    public Dictionary<string, List<string>> Kategorien { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Title keys that mean the same title despite a differing name.</summary>
    public Dictionary<string, string> Aliasse { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Path of the state file (not persisted).</summary>
    [JsonIgnore]
    public string Pfad { get; set; } = string.Empty;

    /// <summary>Reads the state; if the file is missing, it starts empty.</summary>
    public static Zustand Laden(string pfad)
    {
        var z = new Zustand { Pfad = pfad };
        try
        {
            if (File.Exists(pfad))
            {
                var gelesen = JsonSerializer.Deserialize<Zustand>(File.ReadAllText(pfad), Optionen);
                if (gelesen is not null)
                {
                    z.Manifest = new Dictionary<string, Manifestwert>(gelesen.Manifest ?? new(), StringComparer.Ordinal);
                    z.Zuordnungen = new Dictionary<string, Zuordnung>(gelesen.Zuordnungen ?? new(), StringComparer.Ordinal);
                    z.Kategorien = new Dictionary<string, List<string>>(gelesen.Kategorien ?? new(), StringComparer.Ordinal);
                    z.Aliasse = new Dictionary<string, string>(gelesen.Aliasse ?? new(), StringComparer.Ordinal);
                    z.Namen = new Dictionary<string, string>(gelesen.Namen ?? new(), StringComparer.Ordinal);
                    foreach (var k in z.Namen.Keys.ToList())
                    {
                        z.Namen[k] = Textregeln.NameNormalisieren(z.Namen[k]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken state must not prevent the run.
            z = new Zustand { Pfad = pfad };
        }

        return z;
    }

    /// <summary>Writes the state atomically.</summary>
    public void Speichern()
    {
        var ordner = Path.GetDirectoryName(Pfad);
        if (!string.IsNullOrEmpty(ordner))
        {
            Directory.CreateDirectory(ordner);
        }

        var temp = Pfad + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Optionen));
        File.Move(temp, Pfad, overwrite: true);
    }

    /// <summary>Remembers size and image data of a source file.</summary>
    public void Merken(string art, string schluessel, long groesse, int? breite, int? hoehe, int? dauer, string quelle)
    {
        Manifest[Schluessel(art, schluessel)] = new Manifestwert
        {
            Groesse = groesse,
            Breite = breite,
            Hoehe = hoehe,
            Dauer = dauer,
            Quelle = quelle,
            Stand = DateTime.UtcNow
        };
    }

    /// <summary>Values for a placeholder; without an entry 5400 s at 1920x1080.</summary>
    public (int Dauer, int Breite, int Hoehe) ManifestWerte(string art, string schluessel)
    {
        if (Manifest.TryGetValue(Schluessel(art, schluessel), out var m))
        {
            // Placeholder files carry tiny image data - that would make the
            // placeholder unusable, so take the default instead.
            var breite = m.Breite is >= 320 ? m.Breite!.Value : 1920;
            var hoehe = m.Hoehe is >= 240 ? m.Hoehe!.Value : 1080;
            return (m.Dauer is > 60 ? m.Dauer.Value : 5400, breite, hoehe);
        }

        return (5400, 1920, 1080);
    }

    /// <summary>Normalized key for the manifest.</summary>
    public static string Schluessel(string art, string schluessel)
        => art + "|" + schluessel.ToLowerInvariant();
}

/// <summary>Size and image data of a title while the source was present.</summary>
public sealed class Manifestwert
{
    /// <summary>File size in bytes.</summary>
    public long Groesse { get; set; }

    /// <summary>Image width.</summary>
    public int? Breite { get; set; }

    /// <summary>Image height.</summary>
    public int? Hoehe { get; set; }

    /// <summary>Duration in seconds.</summary>
    public int? Dauer { get; set; }

    /// <summary>File the values came from.</summary>
    public string? Quelle { get; set; }

    /// <summary>Time of recording.</summary>
    public DateTime Stand { get; set; }
}

/// <summary>Manual mapping source folder -> TMDB id.</summary>
public sealed class Zuordnung
{
    /// <summary>TMDB id (always set, IMDB/TVDB are resolved).</summary>
    public string TmdbId { get; set; } = string.Empty;

    /// <summary>Provider the id was entered for (tmdb, imdb, tvdb).</summary>
    public string? Anbieter { get; set; }

    /// <summary>Id in the provider's notation.</summary>
    public string? Kennung { get; set; }

    /// <summary>Title according to TMDB.</summary>
    public string? Name { get; set; }

    /// <summary>Year according to TMDB.</summary>
    public int? Jahr { get; set; }

    /// <summary>Where the mapping came from (nfo, manual).</summary>
    public string? Herkunft { get; set; }
}
