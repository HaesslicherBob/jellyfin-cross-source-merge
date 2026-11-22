using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// The naming rules of the merge: comparison key, display name, episode tag.
/// One-to-one reimplementation of the proven Python template. Any deviation
/// here produces duplicate or wrongly named folders, which is why every rule
/// states the reason why it reads this way and not otherwise.
/// </summary>
public static partial class Textregeln
{
    /// <summary>
    /// Source tag of a manual mapping: TMDB stays the bare number, everything
    /// else carries its provider in the value ("tvdb:407966"). That way every
    /// caller can tell whether the tag can be looked up at TMDB.
    /// </summary>
    public static string? Kennung(Zuordnung? zuordnung)
    {
        if (zuordnung is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(zuordnung.TmdbId))
        {
            return zuordnung.TmdbId!.Trim();
        }

        if (string.IsNullOrWhiteSpace(zuordnung.Kennung)
            || string.Equals(zuordnung.Anbieter, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var anbieter = string.IsNullOrWhiteSpace(zuordnung.Anbieter) ? "tvdb" : zuordnung.Anbieter!.Trim().ToLowerInvariant();
        return anbieter + ":" + zuordnung.Kennung!.Trim();
    }

    /// <summary>Tag without a TMDB counterpart, e.g. "tvdb:407966".</summary>
    public static bool IstFremd(string? kennung)
    {
        return !string.IsNullOrWhiteSpace(kennung)
               && !kennung!.All(char.IsDigit)
               && kennung.Contains(':', StringComparison.Ordinal);
    }
    /// <summary>Release tags that never belong to the movie title.</summary>
    private static readonly HashSet<string> SzeneTags = new(StringComparer.Ordinal)
    {
        "german", "deutsch", "ger", "dl", "dubl", "multi", "md",
        "uhd", "uhdbd", "uhdbluray", "bluray", "blu", "ray", "bdrip", "brrip",
        "web", "webrip", "webdl", "hdtv", "dvd", "dvdrip", "hdrip",
        "remux", "hybrid", "complete", "proper", "repack", "internal", "retail",
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
        "ac3", "ac3d", "dts", "dtshd", "dtsma", "dtsx", "eac3", "aac",
        "truehd", "atmos", "ddp", "flac", "opus",
        "hdr", "hdr10", "hdr10plus", "dv", "dovi", "sdr", "hlg",
        "1080p", "2160p", "720p", "480p", "4k", "hkg", "us", "lineup",
        "nf", "amzn", "dsnp", "hmax", "atvp", "itunes", "imax",
        "uncut", "extended", "unrated", "remastered", "kdc", "limited",
        "sub", "subs", "md5", "sample", "cinematic", "bonanza"
    };

    /// <summary>
    /// Comparison key. Same key = the same movie/the same series.
    /// </summary>
    public static string Norm(string name)
    {
        // SCENE PREPARATION: strip release tags so that
        // 'Pulp.Fiction.1994.UHD.HKG...' gets the same key as
        // 'Pulp Fiction (1994)'.
        var n = SzeneBereinigen(name);

        // 1. Resolve accents: 'Pokemon' -> 'pokemon', not 'pok mon'
        var zerlegt = n.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(zerlegt.Length);
        foreach (var c in zerlegt)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        n = sb.ToString().ToLowerInvariant();

        // 2. Separators FIRST: '.' and '_' separate words.
        n = Regex.Replace(n, "[._]+", " ");

        // 3. Strip id tags, REMEMBER the year instead of dropping it - otherwise
        //    remakes collapse ('Aladdin (1992)' == 'Aladdin (2019)').
        n = Regex.Replace(n, @"\s*\[[a-z]+id[=\-]\d+\]", string.Empty);

        // Strip the language code 'DE' BEFORE the year detection
        n = Regex.Replace(n, @"\s+de\s*$", string.Empty);
        n = Regex.Replace(n, @"\s+de\s*(?=\(\d{4}\))", " ");

        var jahr = DisplayYear(n);

        // Remove only the DETECTED year - 'Blade Runner 2049 (2017)'
        // keeps its '2049'.
        if (jahr is not null)
        {
            n = Regex.Replace(n, @"\(" + Regex.Escape(jahr) + @"\)", " ");
            n = Regex.Replace(n, @"(?<=\s)" + Regex.Escape(jahr) + @"(?=\s|$)", " ");
        }

        // 4. Join apostrophes: "Wayne's" -> 'waynes'
        n = Regex.Replace(n, "['\u2019`]", string.Empty);

        // 5. Remaining special characters
        n = Regex.Replace(n, "[^a-z0-9]+", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();

        // 6. Strip the status word 'NEU' when it stands on its own
        n = Regex.Replace(n, @"(^|\s)neu(?=\s|$)", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();

        // Re-attach the year - different years stay separate.
        // Deliberately NO article removal: 'Der Blade Runner (1982)' became
        // 'blade runner' that way and collided with 'Blade Runner (2017)'.
        return jahr is null ? n : $"{n} {jahr}".Trim();
    }

    /// <summary>
    /// Jellyfin's name parser reads four-digit ids from 1900 to 2099 as a
    /// year - namely the LAST such word in the name. 'Training Day (2001)
    /// [tmdbid-2034]' therefore got the year 2034.
    /// </summary>
    public static bool JahresFalle(string? tmdbId)
        => tmdbId is { Length: 4 }
           && (tmdbId.StartsWith("19", StringComparison.Ordinal) || tmdbId.StartsWith("20", StringComparison.Ordinal));

    /// <summary>
    /// Cuts off the year and tag that a self-built name carries at the end -
    /// repeated as well.
    /// </summary>
    public static string TitelOhneAnhang(string? name)
    {
        var rest = (name ?? string.Empty).Trim();
        while (true)
        {
            var j = Regex.Match(rest, @"\s*\((\d{4})\)\s*$");
            if (j.Success)
            {
                rest = rest[..j.Index].TrimEnd();
                continue;
            }

            var k = Regex.Match(rest, @"\s*\[(?:tmdb|tvdb|imdb)id-[0-9a-z]+\]\s*$", RegexOptions.IgnoreCase);
            if (k.Success)
            {
                rest = rest[..k.Index].TrimEnd();
                continue;
            }

            return rest;
        }
    }

    /// <summary>
    /// Brings a self-built name into the one form: 'Title (Year)
    /// [tmdbid-1]'. Duplicate suffixes are collapsed, and with a four-digit
    /// tag, year and tag swap places (JahresFalle). Use only for names from
    /// our own build - not for source folders.
    /// </summary>
    public static string NameNormalisieren(string? name)
    {
        var rest = TitelOhneAnhang(name);
        var ganz = (name ?? string.Empty).Trim();

        if (rest.Length == 0)
        {
            return ganz;
        }

        var k = Regex.Match(ganz, @"\[((tmdb|tvdb|imdb)id-([0-9a-z]+))\]", RegexOptions.IgnoreCase);
        var j = Regex.Match(ganz, @"\((\d{4})\)");

        if (!k.Success)
        {
            return j.Success ? $"{rest} ({j.Groups[1].Value})" : rest;
        }

        // The kind of tag is preserved: [tmdbid-..] stays tmdbid,
        // [tvdbid-..] stays tvdbid. Otherwise a TVDB mapping would turn into
        // a TMDB mapping on the next run.
        var tag = k.Groups[1].Value;
        var nummer = k.Groups[3].Value;
        var istTmdb = string.Equals(k.Groups[2].Value, "tmdb", StringComparison.OrdinalIgnoreCase);
        if (!j.Success)
        {
            return $"{rest} [{tag}]";
        }

        var jahr = j.Groups[1].Value;
        return istTmdb && JahresFalle(nummer)
            ? $"{rest} [{tag}] ({jahr})"
            : $"{rest} ({jahr}) [{tag}]";
    }

    /// <summary>German transliteration - must run BEFORE Norm.</summary>
    public static string UmlautDe(string text) => text
        .Replace("ä", "ae", StringComparison.Ordinal)
        .Replace("ö", "oe", StringComparison.Ordinal)
        .Replace("ü", "ue", StringComparison.Ordinal)
        .Replace("Ä", "Ae", StringComparison.Ordinal)
        .Replace("Ö", "Oe", StringComparison.Ordinal)
        .Replace("Ü", "Ue", StringComparison.Ordinal)
        .Replace("ß", "ss", StringComparison.Ordinal);

    /// <summary>Does the name look like a release name? At least two tags.</summary>
    public static bool IstSzene(string name)
    {
        var teile = Regex.Split(name.ToLowerInvariant(), @"[.\s_\-\[\]()]+");
        var treffer = teile.Count(t => SzeneTags.Contains(t));
        return treffer >= 2;
    }

    /// <summary>Strips release tags, returns 'Title (Year)'.</summary>
    public static string SzeneBereinigen(string name)
    {
        if (!IstSzene(name))
        {
            return name;
        }

        string? jahr = null;
        var rest = name;
        var m = Regex.Match(name, @"\((\d{4})\)\s*$");
        if (m.Success)
        {
            jahr = m.Groups[1].Value;
            rest = name[..m.Index].Trim();
        }

        rest = Regex.Replace(rest, "[._]+", " ");
        var kandidaten = Regex.Split(rest, @"\s+");

        // Cut off from the first release tag onward
        var ende = kandidaten.Length;
        for (var i = 0; i < kandidaten.Length; i++)
        {
            if (SzeneTags.Contains(kandidaten[i].ToLowerInvariant().Trim('-')))
            {
                ende = i;
                break;
            }
        }

        var titel = string.Join(' ', kandidaten.Take(ende)).Trim(' ', '-', '_');
        titel = Regex.Replace(titel, @"\s+", " ").Trim();

        // Remove the year from the title - it only belongs in the parentheses.
        // For 'Hundreds of Beavers 2022 ... (2024)' the years differ;
        // then the year from the scene name applies (2022, the real year).
        string? jahrImTitel = null;
        var m2 = Regex.Match(titel, @"\s+((?:19|20)\d{2})\s*$");
        if (m2.Success)
        {
            jahrImTitel = m2.Groups[1].Value;
            titel = titel[..m2.Index].Trim();
        }

        if (titel.Length == 0)
        {
            return name;
        }

        var jahrFinal = jahrImTitel ?? jahr;
        return jahrFinal is null ? titel : $"{titel} ({jahrFinal})";
    }

    /// <summary>Year from '(1974)' or a free-standing year at the end of the title.</summary>
    public static string? DisplayYear(string name)
    {
        var bereinigt = Regex.Replace(name, @"\s+de\s*$", string.Empty, RegexOptions.IgnoreCase);
        var m = Regex.Match(bereinigt, @"\((\d{4})\)");
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        m = Regex.Match(bereinigt.Trim(), @"[\s\-_]+((?:19|20)\d{2})$");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Clean display name: without [tvdbid=...], without a double year.
    /// The [tmdbid-...] tag is NOT decoration - Jellyfin maps through it -
    /// so it is pulled out and re-attached at the end.
    /// </summary>
    public static string Canonical(string name)
    {
        var mTag = Regex.Match(name, @"\s*\[((?:tmdb|tvdb|imdb)id-[0-9a-z]+)\]\s*$", RegexOptions.IgnoreCase);
        var tag = mTag.Success ? $" [{mTag.Groups[1].Value}]" : string.Empty;

        name = SzeneBereinigen(name);
        var clean = Regex.Replace(name, @"\s*\[[a-z]+id[=\-]\d+\]", string.Empty).Trim();

        // Underscores are separators, not hyphens
        clean = clean.Replace('_', ' ').Trim();
        clean = Regex.Replace(clean, @"\s+", " ");

        // DE CASE WITHOUT PARENTHESES: 'Movie 2024 DE' -> 'Movie (2024)'
        clean = Regex.Replace(clean, @"\(((?:19|20)\d{2})\)\s+DE\s*$", "($1)");

        var mOhne = Regex.Match(clean, @"[\s\-.]+((?:19|20)\d{2})\s+DE\s*$");
        if (mOhne.Success)
        {
            var j = mOhne.Groups[1].Value;
            clean = clean[..mOhne.Index].Trim();
            return $"{clean} ({j}){tag}";
        }

        var y = DisplayYear(name);
        if (y is null)
        {
            return clean.Length > 0 ? clean + tag : clean;
        }

        // Strip the language code 'DE' directly before the year parentheses -
        // only when it stands alone ('Deutschland (2019)' stays intact).
        clean = Regex.Replace(clean, @"\s+DE\s*(?=\(" + Regex.Escape(y) + @"\))", " ");

        // DOUBLE YEAR: remove the number before the parentheses only when it
        // EXACTLY matches the year inside the parentheses.
        clean = Regex.Replace(clean, @"[\s\-_.]+" + Regex.Escape(y) + @"\s*(?=\(" + Regex.Escape(y) + @"\))", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        if (clean.Contains($"({y})", StringComparison.Ordinal))
        {
            return clean + tag;
        }

        // The year stood only as a number at the end ('Heidi - 1974'): add parentheses
        clean = Regex.Replace(clean, @"[\s\-_]+" + Regex.Escape(y) + @"\s*$", string.Empty).Trim();
        return $"{clean} ({y}){tag}";
    }

    /// <summary>
    /// Display name of a source folder. The NFO title is used ONLY for scene
    /// names - with normal folder names the NFO often swallows the
    /// part number ('Austin Powers 2' -> 'Austin Powers').
    /// </summary>
    public static string DisplayName(string entry, string pfad)
    {
        var jahr = DisplayYear(entry);
        if (IstSzene(entry))
        {
            var titel = Nfo.Titel(pfad);
            if (!string.IsNullOrWhiteSpace(titel))
            {
                titel = Regex.Replace(titel.Trim(), @"\s*\(\d{4}\)\s*$", string.Empty).Trim();
                if (titel.Length > 0)
                {
                    return jahr is null ? titel : $"{titel} ({jahr})";
                }
            }
        }

        return Canonical(entry);
    }

    /// <summary>Only what actually breaks in the file system is replaced.</summary>
    public static string NameSaeubern(string titel)
    {
        var t = (titel ?? string.Empty).Replace('/', '-').Replace("\0", string.Empty, StringComparison.Ordinal);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = t.Trim('.');

        // Whatever this system does not allow in a file name is removed here:
        // on Windows, 'Dune: Part Two' turns into 'Dune - Part Two'.
        return Betriebssystem.Dateiname(t);
    }

    /// <summary>TMDB id from a name ('... [tmdbid-123]') or null.</summary>
    public static string? TmdbAusNamen(string name)
    {
        var m = Regex.Match(name, @"\[(?:tmdb|themoviedb)id[=\-](\d+)\]", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        var tid = m.Groups[1].Value;
        // '0' is the source's placeholder for 'unknown'
        return tid.Trim('0').Length == 0 ? null : tid;
    }

    /// <summary>Season and episodes from a file name ('S01E02', 'S01E02E03').</summary>
    public static (int Staffel, int[] Folgen)? SeKey(string dateiname)
    {
        var m = Regex.Match(dateiname, @"[Ss](\d{1,2})[Ee](\d{1,3})(?:[Ee](\d{1,3}))?");
        if (!m.Success)
        {
            return null;
        }

        var staffel = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var folgen = new List<int> { int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) };
        if (m.Groups[3].Success)
        {
            folgen.Add(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return (staffel, folgen.ToArray());
    }

    /// <summary>Sample file?</summary>
    public static bool IstSample(string pfad)
    {
        var teile = pfad.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (teile.Take(Math.Max(teile.Length - 1, 0)).Any(t => string.Equals(t, "sample", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var stamm = Path.GetFileNameWithoutExtension(pfad).ToLowerInvariant();
        return Regex.IsMatch(stamm, @"(^|[\.\-_])sample([\.\-_]|$)");
    }

    /// <summary>Year from a series folder name, also with suffixes behind it.</summary>
    public static string? SerienJahr(string name)
    {
        var treffer = Regex.Matches(name, @"\((\d{4})\)");
        if (treffer.Count > 0)
        {
            return treffer[^1].Groups[1].Value;
        }

        var m = Regex.Match(name, @"(?:^|[\s.])(\d{4})(?:$|[\s.])");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Group key of a series: primarily the TMDB id (unique), otherwise the
    /// base name plus year so that remakes stay apart.
    /// </summary>
    public static string SerienKey(string name)
    {
        var tid = TmdbAusNamen(name);
        if (tid is not null)
        {
            return "tmdb:" + tid;
        }

        var n = Norm(UmlautDe(name));
        var jahr = SerienJahr(name);
        n = Regex.Replace(n, @"\s*\(\d{4}\)\s*$", string.Empty);
        n = Regex.Replace(n, @"\s+\d{4}$", string.Empty).Trim();
        return jahr is null ? n : $"{n}#{jahr}";
    }
}
