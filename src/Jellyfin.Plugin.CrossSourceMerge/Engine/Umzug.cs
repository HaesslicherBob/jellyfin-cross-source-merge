using System.Globalization;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Relocation of the source list: up to 2.2.10 a row carried two folders
/// (movies and series). Since 2.2.11 a row is a folder - that way rank and
/// source tag can be set per folder; the rank sorts the sources but does not
/// appear in any file name.
/// </summary>
public static class Umzug
{
    /// <summary>
    /// Are legacy rows (two folders in one row) still in the configuration?
    /// </summary>
    public static bool Noetig(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg)
        => cfg.Quellen.Any(q => string.IsNullOrWhiteSpace(q.Pfad)
            && (!string.IsNullOrWhiteSpace(q.FilmePfad) || !string.IsNullOrWhiteSpace(q.SerienPfad)));

    /// <summary>
    /// Splits legacy rows: one folder per row, every source tag and every rank
    /// only once. The movie folders keep tag and rank; the series folders get
    /// "&lt;tag&gt; Serien" and ranks above the highest so far, so that the old
    /// order is preserved.
    /// </summary>
    /// <returns>true when something was changed.</returns>
    public static bool Anwenden(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg)
    {
        if (!Noetig(cfg))
        {
            RaengeEindeutig(cfg);
            return false;
        }

        var filmStandard = Kategorien.Standard(cfg.Kategorien, false)?.Name ?? string.Empty;
        var serieStandard = Kategorien.Standard(cfg.Kategorien, true)?.Name ?? string.Empty;
        var hoechster = cfg.Quellen.Count == 0 ? 0 : cfg.Quellen.Max(q => q.Rang);
        var neu = new List<QuelleKonfig>();

        foreach (var q in cfg.Quellen)
        {
            if (!string.IsNullOrWhiteSpace(q.Pfad))
            {
                neu.Add(q);
                continue;
            }

            var kennung = string.IsNullOrWhiteSpace(q.Kennung) ? "Quelle" : q.Kennung.Trim();
            var filme = q.FilmePfad?.Trim() ?? string.Empty;
            var serien = q.SerienPfad?.Trim() ?? string.Empty;
            var gleich = filme.Length > 0 && string.Equals(filme, serien, StringComparison.Ordinal);

            if (filme.Length > 0)
            {
                neu.Add(new QuelleKonfig
                {
                    Kennung = kennung,
                    Art = q.Art,
                    Rang = q.Rang,
                    Pfad = filme,
                    Schatten = q.Schatten,
                    StandardKategorie = filmStandard
                });
            }

            if (serien.Length > 0)
            {
                var rang = q.Rang;
                if (!gleich)
                {
                    // Own rank: each rank may only be assigned once.
                    hoechster++;
                    rang = hoechster;
                }

                // Since 2.2.12 uniqueness counts per media kind: the series
                // row may carry the same tag as the movie row, then both
                // trees show "3 NAS". Only when the kinds cannot be told
                // apart (no default category) does it need the suffix -
                // otherwise the entries collide.
                var zusatz = filmStandard.Length > 0 && serieStandard.Length > 0 ? string.Empty : " Serien";
                neu.Add(new QuelleKonfig
                {
                    Kennung = gleich ? kennung : kennung + zusatz,
                    Art = q.Art,
                    Rang = rang,
                    Pfad = serien,
                    Schatten = q.Schatten,
                    StandardKategorie = serieStandard
                });
            }

            // Clear the old fields so that the relocation does not apply again.
            q.FilmePfad = string.Empty;
            q.SerienPfad = string.Empty;
        }

        cfg.Quellen = neu;
        RaengeEindeutig(cfg);
        return true;
    }

    /// <summary>
    /// Ensures that no rank is assigned twice: the second bearer of a rank
    /// moves behind the highest one. Source tags stay untouched.
    /// </summary>
    public static void RaengeEindeutig(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg)
    {
        var vergeben = new HashSet<int>();
        var hoechster = cfg.Quellen.Count == 0 ? 0 : cfg.Quellen.Max(q => q.Rang);
        foreach (var q in cfg.Quellen.OrderBy(q => q.Rang).ThenBy(q => q.Kennung, StringComparer.OrdinalIgnoreCase))
        {
            if (vergeben.Add(q.Rang))
            {
                continue;
            }

            hoechster++;
            q.Rang = hoechster;
            vergeben.Add(q.Rang);
        }
    }

    /// <summary>
    /// Media kinds that a row serves: movies, series or both (without a
    /// default category the source applies to both kinds).
    /// </summary>
    private static (bool Filme, bool Serien) Arten(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg, QuelleKonfig q)
    {
        return Kategorien.Art(cfg.Kategorien, q.StandardKategorie) switch
        {
            true => (false, true),
            false => (true, false),
            _ => (true, true)
        };
    }

    /// <summary>
    /// Source tags that are assigned more than once within one media kind. The
    /// tag appears in the file name, and the plan maps the entries per title
    /// through it - two movie sources with the same tag would plan the same
    /// entry twice and never map the file of the second source.
    /// Across media kinds the same tag is harmless: the trees are separate,
    /// and "3 NAS" for movies and "3 NAS" for series reads better in Jellyfin
    /// than "3 NAS" and "3 NAS Serien".
    /// </summary>
    public static List<string> DoppelteKennungen(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg)
    {
        var doppelt = new List<string>();

        foreach (var gruppe in cfg.Quellen
                     .Where(q => !string.IsNullOrWhiteSpace(q.Kennung))
                     .GroupBy(q => q.Kennung.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var zeilen = gruppe.Select(q => Arten(cfg, q)).ToList();
            if (zeilen.Count(a => a.Filme) > 1 || zeilen.Count(a => a.Serien) > 1)
            {
                doppelt.Add(gruppe.Key);
            }
        }

        return doppelt;
    }

    /// <summary>Warning hints for the front end (duplicate tag/rank).</summary>
    public static List<string> Hinweise(Jellyfin.Plugin.CrossSourceMerge.PluginConfiguration cfg)
    {
        var liste = new List<string>();

        foreach (var kennung in DoppelteKennungen(cfg))
        {
            liste.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Source tag '{0}' is duplicated within one media kind - only once per media kind.",
                kennung));
        }

        foreach (var gruppe in cfg.Quellen.GroupBy(q => q.Rang).Where(g => g.Count() > 1))
        {
            liste.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Rank {0} is assigned {1} times.",
                gruppe.Key,
                gruppe.Count()));
        }

        return liste;
    }
}
