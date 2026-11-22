using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Inventory per source folder: what is there, which tag it carries,
/// and what the target name would be. This is the basis of the list
/// "Missing mappings" - one row per source folder, not per title, because
/// the same movie can come from three sources with three different tags.
/// </summary>
public static class Bestand
{
    private static readonly object Sperre = new();
    private static readonly Dictionary<string, (Ergebnis Liste, DateTime Stand)> Zwischen = new(StringComparer.Ordinal);

    /// <summary>Rows of one kind and the number of folders without their own medium.</summary>
    public sealed class Ergebnis
    {
        /// <summary>Rows of the list.</summary>
        public IReadOnlyList<Zeile> Zeilen { get; init; } = Array.Empty<Zeile>();

        /// <summary>Folders that hold only a stream reference (no own medium).</summary>
        public int OhneMedium { get; set; }
    }

    /// <summary>One row of the inventory list.</summary>
    public sealed class Zeile
    {
        /// <summary>Movies or series.</summary>
        public string Art { get; init; } = string.Empty;

        /// <summary>Source tag ("1 Local", "2 Stream", "3 NAS").</summary>
        public string Quelle { get; init; } = string.Empty;

        /// <summary>Full path of the source folder.</summary>
        public string Pfad { get; init; } = string.Empty;

        /// <summary>Folder name in the source.</summary>
        public string Ordner { get; init; } = string.Empty;

        /// <summary>Largest file in the folder (empty for series).</summary>
        public string? Datei { get; init; }

        /// <summary>Tag that would apply now (name, NFO or manual mapping).</summary>
        public string? IstId { get; set; }

        /// <summary>Where the tag comes from: hand, name, nfo or empty.</summary>
        public string IstHerkunft { get; set; } = string.Empty;

        /// <summary>Display name from the folder name.</summary>
        public string IstName { get; set; } = string.Empty;

        /// <summary>Year from the folder name.</summary>
        public int? IstJahr { get; set; }

        /// <summary>Provider of the stored manual mapping, if there is one.</summary>
        public string? HandAnbieter { get; set; }

        /// <summary>Tag in the provider's notation.</summary>
        public string? HandKennung { get; set; }

        /// <summary>Name from the manual mapping.</summary>
        public string? HandName { get; set; }

        /// <summary>Year from the manual mapping.</summary>
        public int? HandJahr { get; set; }

        /// <summary>Target folder name that the merge would build.</summary>
        public string Ziel { get; set; } = string.Empty;

        /// <summary>One of: zugeordnet, ohne-kennung, ohne-info.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Source currently not reachable.</summary>
        public bool Fehlt { get; set; }

        /// <summary>
        /// Categories set for this title. Empty means: the default category
        /// applies (or a pattern from the configuration).
        /// </summary>
        public List<string> Kategorien { get; set; } = new();
    }

    /// <summary>Discards the cache (after every manual mapping).</summary>
    public static void Verwerfen()
    {
        lock (Sperre)
        {
            Zwischen.Clear();
        }
    }

    /// <summary>List of all source folders of one kind.</summary>
    public static Ergebnis Ermitteln(
        PluginConfiguration cfg,
        bool serie,
        TmdbIndex index,
        Zustand zustand,
        ILogger log)
    {
        var art = serie ? "series" : "movie";
        lock (Sperre)
        {
            if (Zwischen.TryGetValue(art, out var alt) && (DateTime.UtcNow - alt.Stand) < TimeSpan.FromSeconds(120))
            {
                return alt.Liste;
            }
        }

        var zusatz = new Dictionary<string, (string Titel, string? Jahr)>(StringComparer.Ordinal);
        foreach (var (schluessel, name) in zustand.Namen)
        {
            var trenner = schluessel.IndexOf('|');
            if (trenner < 0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = schluessel[(trenner + 1)..];
            if (id.StartsWith("tmdb:", StringComparison.Ordinal))
            {
                id = id[5..];
            }

            zusatz[id] = (name, Textregeln.DisplayYear(name));
        }

        foreach (var z in zustand.Zuordnungen.Values)
        {
            if (Textregeln.Kennung(z) is { } tag && !string.IsNullOrWhiteSpace(z.Name))
            {
                zusatz[tag] = (z.Name!, z.Jahr?.ToString(CultureInfo.InvariantCulture));
            }
        }

        var zeilen = new List<Zeile>();
        var ohneMedium = 0;
        foreach (var q in MergeLauf.Quellen(cfg, serie))
        {
            var erreichbar = q.Erreichbar();
            if (!erreichbar)
            {
                log.LogWarning("Inventory: source {Kennung} ({Pfad}) not reachable", q.Kennung, q.Pfad);
            }

            string[] ordner;
            try
            {
                ordner = erreichbar ? Directory.GetDirectories(q.Pfad) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Inventory: source {Kennung} not readable ({Fehler})", q.Kennung, ex.Message);
                ordner = Array.Empty<string>();
            }

            foreach (var pfad in ordner.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(pfad);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                // Without its own medium there is nothing to map: a .strm in
                // the NAS source is a stream reference, not a NAS file.
                var medium = BesteDatei(pfad, q.Art);
                if (medium is null)
                {
                    ohneMedium++;
                    continue;
                }

                zustand.Zuordnungen.TryGetValue(name, out var hand);
                var lesen = Lesen(name);

                var zeile = new Zeile
                {
                    Art = art,
                    Quelle = q.Kennung,
                    Pfad = pfad,
                    Ordner = name,
                    Datei = serie ? null : medium,
                    IstName = lesen.Titel,
                    IstJahr = lesen.Jahr,
                    HandAnbieter = hand?.Anbieter,
                    HandKennung = hand?.Kennung,
                    HandName = hand?.Name,
                    HandJahr = hand?.Jahr,
                    Fehlt = !erreichbar
                };

                if (Textregeln.Kennung(hand) is { } handTag)
                {
                    zeile.IstId = handTag;
                    zeile.IstHerkunft = "hand";
                }
                else
                {
                    var tid = Textregeln.TmdbAusNamen(name);
                    if (tid is not null)
                    {
                        zeile.IstId = tid;
                        zeile.IstHerkunft = "name";
                    }
                    else
                    {
                        tid = Nfo.TmdbId(pfad);
                        if (tid is not null)
                        {
                            zeile.IstId = tid;
                            zeile.IstHerkunft = "nfo";
                        }
                    }
                }

                // What the library knows unambiguously by name is not a
                // missing mapping - the merge resolves it on its own.
                if (string.IsNullOrWhiteSpace(zeile.IstId))
                {
                    var ausBibliothek = index.IdFuer(
                        Textregeln.TitelOhneAnhang(zeile.IstName),
                        zeile.IstJahr?.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(ausBibliothek))
                    {
                        zeile.IstId = ausBibliothek;
                        zeile.IstHerkunft = "bibliothek";
                    }
                }

                var ersatz = zeile.IstJahr is null
                    ? zeile.IstName
                    : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", zeile.IstName, zeile.IstJahr);
                // Categories: first at the title (tag), then at the source folder.
                var artKurz = serie ? "series" : "movie";
                var werkSchluessel = string.IsNullOrWhiteSpace(zeile.IstId)
                    ? null
                    : Textregeln.IstFremd(zeile.IstId) ? zeile.IstId : "tmdb:" + zeile.IstId;
                if (werkSchluessel is not null
                    && zustand.Kategorien.TryGetValue((artKurz + "|" + werkSchluessel).ToLowerInvariant(), out var amWerk))
                {
                    zeile.Kategorien.AddRange(amWerk);
                }

                if (zeile.Kategorien.Count == 0
                    && zustand.Kategorien.TryGetValue((q.Kennung + "|" + name).ToLowerInvariant(), out var amOrdner))
                {
                    zeile.Kategorien.AddRange(amOrdner);
                }

                zeile.Ziel = Scannen.ZielName(zeile.IstId, ersatz, index, zusatz);
                var info = string.IsNullOrWhiteSpace(zeile.IstId) ? null : index.Info(zeile.IstId);
                // A foreign tag (tvdb:...) has no TMDB entry and
                // does not need one - it is mapped anyway.
                zeile.Status = string.IsNullOrWhiteSpace(zeile.IstId)
                    ? "ohne-kennung"
                    : Textregeln.IstFremd(zeile.IstId) || info is not null ? "zugeordnet" : "ohne-info";

                zeilen.Add(zeile);
            }
        }

        var ergebnis = new Ergebnis { Zeilen = zeilen, OhneMedium = ohneMedium };
        lock (Sperre)
        {
            Zwischen[art] = (ergebnis, DateTime.UtcNow);
        }

        return ergebnis;
    }

    /// <summary>
    /// Title and year from a folder name. Dots and underscores count as
    /// separators when the name has no spaces otherwise - that is how
    /// dotted folder names arrive ("Ein.Film.Titel.2003").
    /// </summary>
    private static (string Titel, int? Jahr) Lesen(string name)
    {
        var text = name;
        if (!text.Contains(' ') && (text.Count(c => c == '.') >= 2 || text.Contains('_')))
        {
            text = text.Replace('.', ' ').Replace('_', ' ');
        }
        else if (text.Contains(' ') && text.Contains('_'))
        {
            text = text.Replace('_', ' ');
        }

        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Markers that say nothing about the movie.
        text = Regex.Replace(text, @"\s+(?:NEU|DE|GER|German)$", string.Empty, RegexOptions.IgnoreCase).Trim();
        var jahrText = Textregeln.DisplayYear(text);
        var titel = Textregeln.TitelOhneAnhang(text);
        if (jahrText is not null)
        {
            titel = Regex.Replace(titel, @"[\s\-_.]+" + jahrText + @"$", string.Empty).Trim();
        }

        titel = Textregeln.NameSaeubern(titel);
        var jahr = int.TryParse(jahrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zahl) ? zahl : (int?)null;
        return (titel, jahr);
    }

    /// <summary>
    /// Largest usable file of a folder - by the same rules as the merge:
    /// stream sources count only .strm, NAS sources do not (.strm is a last
    /// resort there), sample files never.
    /// </summary>
    private static string? BesteDatei(string ordner, Quellenart art)
    {
        try
        {
            string? beste = null;
            long groesse = -1;
            foreach (var datei in Directory.EnumerateFiles(ordner, "*", SearchOption.AllDirectories))
            {
                var endung = Path.GetExtension(datei).ToLowerInvariant();
                if (!Scannen.VideoEndungen.Contains(endung) || Textregeln.IstSample(datei))
                {
                    continue;
                }

                if (art == Quellenart.Stream && endung != ".strm")
                {
                    continue;
                }

                if (art == Quellenart.Nas && endung == ".strm")
                {
                    continue;
                }

                long laenge;
                try
                {
                    laenge = new FileInfo(datei).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                if (laenge > groesse)
                {
                    groesse = laenge;
                    beste = datei;
                }
            }

            return beste;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
