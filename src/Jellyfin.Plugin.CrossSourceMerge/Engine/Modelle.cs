namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Where the file comes from - decides what is created in the target tree.</summary>
public enum Quellenart
{
    /// <summary>Real file on disk.</summary>
    Lokal,

    /// <summary>Only .strm references (streaming).</summary>
    Stream,

    /// <summary>Network drive; if it fails, entries remain in place.</summary>
    Nas
}

/// <summary>A source folder as it appears in the configuration.</summary>
public sealed class Quelle
{
    /// <summary>Display tag, ends up in the file name ('1 Lokal').</summary>
    public string Kennung { get; set; } = string.Empty;

    /// <summary>Type of the source.</summary>
    public Quellenart Art { get; set; } = Quellenart.Lokal;

    /// <summary>Lower = more important. Determines display name and file order.</summary>
    public int Rang { get; set; } = 50;

    /// <summary>Folder of the source for this media type.</summary>
    public string Pfad { get; set; } = string.Empty;

    /// <summary>Entries of this source remain in place when it is missing.</summary>
    public bool Schatten { get; set; }

    /// <summary>Is the folder reachable at all?</summary>
    public bool Erreichbar()
    {
        try
        {
            return Directory.Exists(Pfad);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>What a source contributes to a group.</summary>
public sealed class Quellenbeitrag
{
    /// <summary>Root of the source (for path comparisons).</summary>
    public string Wurzel { get; init; } = string.Empty;

    /// <summary>Folder of the title in the source.</summary>
    public string Ordner { get; init; } = string.Empty;

    /// <summary>Chosen file (for movies) - null for series.</summary>
    public string? Datei { get; init; }

    /// <summary>Additional files of the same title in the same source.</summary>
    public List<string> Weitere { get; } = new();

    /// <summary>Episodes per (season, episode) - series only.</summary>
    public Dictionary<(int Staffel, int Folge), List<string>> Folgen { get; init; } = new();

    /// <summary>All files of this contribution (main file first).</summary>
    public IReadOnlyList<string> AlleDateien()
    {
        var liste = new List<string>();
        if (Datei is not null)
        {
            liste.Add(Datei);
        }

        // Sorted so that version 1 and 2 do not swap places between two
        // runs: the order of reading depends on the file system, but the
        // number is written in the file name.
        liste.AddRange(Weitere.OrderBy(w => w, StringComparer.Ordinal));
        return liste;
    }
}

/// <summary>A title (movie or series) across all sources.</summary>
public sealed class Gruppe
{
    /// <summary>Comparison key ('tmdb:693134' or normalized title).</summary>
    public string Schluessel { get; init; } = string.Empty;

    /// <summary>Target folder name for this title.</summary>
    public string Anzeigename { get; set; } = string.Empty;

    /// <summary>TMDB id, if known.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Contributions per source tag.</summary>
    public Dictionary<string, Quellenbeitrag> Beitraege { get; } = new(StringComparer.Ordinal);

    /// <summary>Rank of the source that provided the display name.</summary>
    public int AnzeigeRang { get; set; } = 99;

    /// <summary>Folder of a title that exists only as a NAS reference.</summary>
    public Quellenbeitrag? Notnagel { get; set; }

    /// <summary>Tag of the fallback source.</summary>
    public string NotnagelKennung { get; set; } = string.Empty;
}

/// <summary>One entry in the target tree: what links where.</summary>
public sealed class PlanEintrag
{
    /// <summary>Path in the target tree.</summary>
    public string Ziel { get; init; } = string.Empty;

    /// <summary>Source file; null for shadow entries.</summary>
    public string? Quelle { get; init; }

    /// <summary>Source tag (for the statistics).</summary>
    public string Kennung { get; init; } = string.Empty;

    /// <summary>Title the entry belongs to (comparison key).</summary>
    public string WerkKennung { get; init; } = string.Empty;

    /// <summary>Does the entry belong to a series?</summary>
    public bool IstSerie { get; init; }

    /// <summary>Season (series only).</summary>
    public int Staffel { get; init; }

    /// <summary>Episode (series only).</summary>
    public int Folge { get; init; }
}

/// <summary>Result of a run.</summary>
public sealed class Laufbericht
{
    /// <summary>Number of groups (titles).</summary>
    public int Werke { get; set; }

    /// <summary>Number of planned entries.</summary>
    public int Eintraege { get; set; }

    /// <summary>Newly created links.</summary>
    public int Gesetzt { get; set; }

    /// <summary>Left unchanged.</summary>
    public int Unveraendert { get; set; }

    /// <summary>Errors with path - at most <see cref="Fehlergrenze"/> of them.</summary>
    /// <remarks>
    /// A misconfigured target folder can make every entry fail.
    /// Eighty thousand identical lines help nobody and make the
    /// status file unusable: the first ones are shown, the count is in
    /// <see cref="FehlerZahl"/>.
    /// </remarks>
    public List<string> Fehler { get; } = new();

    /// <summary>Gets the number of all errors, including those not listed.</summary>
    public int FehlerZahl { get; private set; }

    /// <summary>Gets the limit of listed errors.</summary>
    public static int Fehlergrenze => 50;

    /// <summary>Gets how many errors went beyond the list.</summary>
    public int NichtAufgelistet => FehlerZahl - Fehler.Count;

    /// <summary>Count an error and - as long as there is room - record it.</summary>
    public void FehlerMelden(string text)
    {
        FehlerZahl++;
        if (Fehler.Count < Fehlergrenze)
        {
            Fehler.Add(text);
        }
    }

    /// <summary>Links whose source is missing and that remain in place.</summary>
    public int StehenGelassen { get; set; }

    /// <summary>Entries writable in the dry run.</summary>
    public int Geplant { get; set; }

    /// <summary>Entries of a missing source that remain in place.</summary>
    public int Schatten { get; set; }

    /// <summary>Placeholders that were newly created.</summary>
    public int Attrappen { get; set; }

    /// <summary>Links that point to a replacement episode from another source.</summary>
    public int Ersatz { get; set; }

    /// <summary>Counters for diagnosis: target missing / link pointed elsewhere.</summary>
    public Dictionary<string, int> Gruende { get; } = new(StringComparer.Ordinal);
}

/// <summary>A source folder in the configuration.</summary>
public class QuelleKonfig
{
    /// <summary>Gets or sets the display tag in the file name ('1 Lokal', '3 NAS').</summary>
    public string Kennung { get; set; } = string.Empty;

    /// <summary>Gets or sets the type of the source.</summary>
    public Quellenart Art { get; set; } = Quellenart.Lokal;

    /// <summary>Gets or sets the rank. Lower = more important.</summary>
    public int Rang { get; set; } = 50;

    /// <summary>
    /// Gets or sets the folder of this source. One line is one folder -
    /// formerly a line carried two folders (movies and series).
    /// </summary>
    public string Pfad { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category that pre-fills a new entry from this
    /// source. Empty means: the default category of the media type.
    /// </summary>
    public string StandardKategorie { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder of the source for movies (legacy field, only for the relocation).</summary>
    public string FilmePfad { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder of the source for series (legacy field, only for the relocation).</summary>
    public string SerienPfad { get; set; } = string.Empty;

    /// <summary>Gets or sets whether entries remain in place when the source is missing.</summary>
    public bool Schatten { get; set; }
}
