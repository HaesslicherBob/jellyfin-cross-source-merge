namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Categories: per type one default target and any number of further target trees.
/// A title can lie in several categories (a movie also under
/// documentaries).
/// </summary>
public static class Kategorien
{
    /// <summary>Categories of one type, default first.</summary>
    public static List<KategorieKonfig> Liste(IReadOnlyList<KategorieKonfig> cfg, bool istSerie)
    {
        return cfg
            .Where(k => k.IstSerie == istSerie && !string.IsNullOrWhiteSpace(k.Zielpfad))
            .OrderByDescending(k => k.Standard)
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Is the category a series category? null if it does not exist.
    /// </summary>
    public static bool? Art(IReadOnlyList<KategorieKonfig> cfg, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var treffer = cfg.FirstOrDefault(k => string.Equals(k.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return treffer?.IstSerie;
    }

    /// <summary>Names of the categories of one type.</summary>
    public static List<KategorieKonfig> Alle(IReadOnlyList<KategorieKonfig> cfg, bool istSerie)
        => cfg.Where(k => k.IstSerie == istSerie).ToList();

    /// <summary>
    /// Makes sure the old target fields show up as categories.
    /// An existing installation thus keeps its structure.
    /// </summary>
    public static void Auffuellen(IList<KategorieKonfig> liste, string? zielFilme, string? zielSerien)
    {
        if (liste.Count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(zielFilme))
        {
            liste.Add(new KategorieKonfig { Name = "Filme", IstSerie = false, Zielpfad = zielFilme, Standard = true });
        }

        if (!string.IsNullOrWhiteSpace(zielSerien))
        {
            liste.Add(new KategorieKonfig { Name = "Serien", IstSerie = true, Zielpfad = zielSerien, Standard = true });
        }
    }

    /// <summary>Default category of one type, or the first one.</summary>
    public static KategorieKonfig? Standard(IReadOnlyList<KategorieKonfig> cfg, bool istSerie)
    {
        var liste = Liste(cfg, istSerie);
        return liste.FirstOrDefault(k => k.Standard) ?? liste.FirstOrDefault();
    }

    /// <summary>
    /// {title key: {category names}}. Order: mapping at the title,
    /// then mapping at the source folder, then the default category.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Aufloesen(
        IReadOnlyList<KategorieKonfig> cfg,
        bool istSerie,
        IReadOnlyDictionary<string, Gruppe> gruppen,
        Zustand zustand,
        IReadOnlyList<QuelleKonfig>? quellen = null)
    {
        var art = istSerie ? "series" : "movie";

        // Every configured category is allowed - including those of the other
        // type, since a title may be placed there explicitly.
        var erlaubt = cfg
            .Where(k => !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => k.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standard = Standard(cfg, istSerie)?.Name;
        var erg = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (schluessel, g) in gruppen)
        {
            var namen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (zustand.Kategorien.TryGetValue(art + "|" + schluessel.ToLowerInvariant(), out var eigen))
            {
                namen.UnionWith(eigen);
            }

            if (namen.Count == 0)
            {
                foreach (var (kennung, beitrag) in g.Beitraege)
                {
                    foreach (var ordner in new[] { beitrag.Ordner, beitrag.Wurzel }.Where(o => !string.IsNullOrWhiteSpace(o)))
                    {
                        var k = kennung + "|" + ordner;
                        if (zustand.Kategorien.TryGetValue(k.ToLowerInvariant(), out var ueber))
                        {
                            namen.UnionWith(ueber);
                        }
                    }
                }
            }

            namen.IntersectWith(erlaubt);
            if (namen.Count == 0)
            {
                namen.UnionWith(MusterTreffer(cfg, istSerie, g));
            }

            if (namen.Count == 0)
            {
                // Before the default category of the type, the category that
                // hangs on the source applies: one row per folder, so one
                // default per folder.
                // The same tag may stand for movies and series; here only what
                // fits the running media type counts.
                var ausQuelle = (quellen ?? Array.Empty<QuelleKonfig>())
                    .Where(q => !string.IsNullOrWhiteSpace(q.StandardKategorie))
                    .Where(q => Art(cfg, q.StandardKategorie) is not { } a || a == istSerie)
                    .GroupBy(q => q.Kennung, StringComparer.Ordinal)
                    .ToDictionary(g2 => g2.Key, g2 => g2.First(), StringComparer.Ordinal);

                var gewaehlt = g.Beitraege.Keys
                    .Select(k => ausQuelle.TryGetValue(k, out var qq) ? qq : null)
                    .Where(qq => qq is not null)
                    .OrderBy(qq => qq!.Rang)
                    .Select(qq => qq!.StandardKategorie.Trim())
                    .FirstOrDefault(kat => erlaubt.Contains(kat));

                if (!string.IsNullOrWhiteSpace(gewaehlt))
                {
                    namen.Add(gewaehlt);
                }
                else if (standard is not null)
                {
                    namen.Add(standard);
                }
            }

            erg[schluessel] = namen;
        }

        return erg;
    }

    /// <summary>Categories whose patterns match the title.</summary>
    private static IEnumerable<string> MusterTreffer(
        IReadOnlyList<KategorieKonfig> cfg,
        bool istSerie,
        Gruppe g)
    {
        var texte = new List<string> { g.Anzeigename };
        texte.AddRange(g.Beitraege.Values.Select(b => Path.GetFileName(b.Ordner)));

        foreach (var kat in Alle(cfg, istSerie))
        {
            if (string.IsNullOrWhiteSpace(kat.Muster))
            {
                continue;
            }

            foreach (var muster in kat.Muster.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (texte.Any(t => t.Contains(muster, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return kat.Name;
                    break;
                }
            }
        }
    }

    /// <summary>Key of a title for the state file.</summary>
    public static string Medium(bool istSerie, string schluessel)
        => (istSerie ? "series|" : "movie|") + schluessel.ToLowerInvariant();
}

/// <summary>A target tree with a name - 'Filme', 'Dokumentationen', 'Serien'.</summary>
public sealed class KategorieKonfig
{
    /// <summary>Name of the category.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Does the category hold series (true) or movies.</summary>
    public bool IstSerie { get; set; }

    /// <summary>Target folder of this category.</summary>
    public string Zielpfad { get; set; } = string.Empty;

    /// <summary>Titles without their own mapping land here (exactly one per type).</summary>
    public bool Standard { get; set; }

    /// <summary>
    /// Pattern for the automatic mapping, separated by semicolons.
    /// A title lands in this category when title or source folder contains
    /// a pattern (case-insensitive).
    /// </summary>
    public string Muster { get; set; } = string.Empty;
}
