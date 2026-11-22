namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Turns groups into the entries of the target tree.</summary>
public static class Planen
{
    /// <summary>
    /// Movies: one folder per title, inside it one file per source.
    /// 'Film (2024) [tmdbid-1] - 1 Lokal.mkv'
    /// </summary>
    public static List<PlanEintrag> Filme(
        IReadOnlyDictionary<string, Gruppe> gruppen,
        string zielwurzel,
        IReadOnlyList<Quelle> quellen,
        IReadOnlySet<string>? nurSchluessel = null)
    {
        var plan = new List<PlanEintrag>();
        var reihenfolge = quellen.OrderBy(q => q.Rang).ThenBy(q => q.Kennung, StringComparer.Ordinal).ToList();

        foreach (var g in gruppen.Values
            .Where(g => nurSchluessel is null || nurSchluessel.Contains(g.Schluessel))
            .OrderBy(g => g.Schluessel, StringComparer.Ordinal))
        {
            var ordner = Path.Combine(zielwurzel, g.Anzeigename);

            foreach (var q in reihenfolge)
            {
                if (q.Kennung == g.NotnagelKennung && g.Notnagel is not null && !g.Beitraege.ContainsKey(q.Kennung))
                {
                    plan.Add(new PlanEintrag
                    {
                        Ziel = Path.Combine(ordner, Dateiname(g.Anzeigename, q.Kennung, 0, g.Notnagel.Datei!)),
                        Quelle = g.Notnagel.Datei,
                        Kennung = q.Kennung,
                        WerkKennung = g.Schluessel
                    });
                    continue;
                }

                if (!g.Beitraege.TryGetValue(q.Kennung, out var beitrag))
                {
                    continue;
                }

                // Multiple qualities of the same source are counted, a single
                // version stays without a number: Jellyfin shows the text after
                // the last " - " as the version name, "3 NAS 1" with a single
                // provider would only be noise.
                var alle = beitrag.AlleDateien();
                var nr = 1;
                foreach (var datei in alle)
                {
                    plan.Add(new PlanEintrag
                    {
                        Ziel = Path.Combine(ordner, Dateiname(g.Anzeigename, q.Kennung, alle.Count > 1 ? nr : 0, datei)),
                        Quelle = datei,
                        Kennung = q.Kennung,
                        WerkKennung = g.Schluessel
                    });
                    nr++;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Series: '<Werk>/Season 01/<Werk> - S01E01 - 1 Lokal.mkv'.
    /// </summary>
    public static List<PlanEintrag> Serien(
        IReadOnlyDictionary<string, Gruppe> gruppen,
        string zielwurzel,
        IReadOnlyList<Quelle> quellen,
        IReadOnlySet<string>? nurSchluessel = null)
    {
        var plan = new List<PlanEintrag>();
        var reihenfolge = quellen.OrderBy(q => q.Rang).ThenBy(q => q.Kennung, StringComparer.Ordinal).ToList();

        foreach (var g in gruppen.Values
            .Where(g => nurSchluessel is null || nurSchluessel.Contains(g.Schluessel))
            .OrderBy(g => g.Schluessel, StringComparer.Ordinal))
        {
            var ordner = Path.Combine(zielwurzel, g.Anzeigename);
            var alleFolgen = new SortedDictionary<(int Staffel, int Folge), Dictionary<string, string>>();

            foreach (var (kennung, beitrag) in g.Beitraege)
            {
                foreach (var (fach, dateien) in beitrag.Folgen)
                {
                    if (dateien.Count == 0)
                    {
                        continue;
                    }

                    if (!alleFolgen.TryGetValue(fach, out var jeQuelle))
                    {
                        jeQuelle = new Dictionary<string, string>(StringComparer.Ordinal);
                        alleFolgen[fach] = jeQuelle;
                    }

                    jeQuelle[kennung] = dateien[0];
                }
            }

            foreach (var (fach, jeQuelle) in alleFolgen)
            {
                var staffel = $"Season {fach.Staffel:D2}";
                foreach (var q in reihenfolge)
                {
                    if (!jeQuelle.TryGetValue(q.Kennung, out var datei))
                    {
                        continue;
                    }

                    // Each episode has exactly one file here (dateien[0]), so
                    // there is nothing to count - and no rank in the name: the
                    // tag already distinguishes the sources.
                    var folgentag = $"S{fach.Staffel:D2}E{fach.Folge:D2}";
                    var endung = Path.GetExtension(datei).ToLowerInvariant();
                    var name = $"{g.Anzeigename} - {folgentag} - {q.Kennung}{endung}";
                    plan.Add(new PlanEintrag
                    {
                        Ziel = Path.Combine(ordner, staffel, name),
                        Quelle = datei,
                        Kennung = q.Kennung,
                        WerkKennung = g.Schluessel,
                        IstSerie = true,
                        Staffel = fach.Staffel,
                        Folge = fach.Folge
                    });
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// File name of a movie version: "Titel (Jahr) [id] - 3 NAS 1.mkv", with only
    /// one version of this source without a number: "... - 3 NAS.mkv". The tag
    /// stays unchanged (no "3 NAS2"), the number sits at the end and counts
    /// the files that the same source has for this title (different
    /// qualities), starting at 1. 0 means: do not count.
    /// The rank no longer appears in any file name - it only orders the
    /// sources (order in the plan, and whose title provides the folder name);
    /// in the name it did not distinguish anything the tag does not already say.
    /// Note: no additional " - " before the number, Jellyfin splits at the
    /// last " - " - otherwise every source would become a separate movie.
    /// </summary>
    private static string Dateiname(string anzeige, string kennung, int nummer, string quelldatei)
    {
        var endung = Path.GetExtension(quelldatei).ToLowerInvariant();
        return nummer > 0
            ? $"{anzeige} - {kennung} {nummer}{endung}"
            : $"{anzeige} - {kennung}{endung}";
    }
}
