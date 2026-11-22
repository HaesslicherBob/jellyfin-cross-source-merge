using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Reads the source trees and groups them into titles.</summary>
public static class Scannen
{
    /// <summary>Extensions that count as video.</summary>
    public static readonly HashSet<string> VideoEndungen = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".avi", ".mp4", ".m4v", ".ts", ".ogm", ".m2ts", ".iso", ".strm"
    };

    /// <summary>
    /// Movies: one folder = one movie, the largest video file counts.
    /// </summary>
    public static Dictionary<string, Gruppe> Filme(
        IReadOnlyList<Quelle> quellen,
        TmdbIndex index,
        IReadOnlyDictionary<string, (string Titel, string? Jahr)> zusatz,
        ILogger log,
        IReadOnlyDictionary<string, Zuordnung>? zuordnungen = null)
    {
        var gruppen = new Dictionary<string, Gruppe>(StringComparer.Ordinal);
        var notnagel = new List<(string Name, string Pfad, Quelle Q, string Datei)>();

        foreach (var q in quellen)
        {
            if (!q.Erreichbar())
            {
                log.LogWarning("Source {Kennung}: {Pfad} not reachable, skipped", q.Kennung, q.Pfad);
                continue;
            }

            string[] eintraege;
            try
            {
                eintraege = Directory.GetDirectories(q.Pfad);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Source {Kennung}: {Pfad} not readable ({Fehler})", q.Kennung, q.Pfad, ex.Message);
                continue;
            }

            foreach (var ordner in eintraege.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(ordner);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                // One folder is one title. The largest file is the first
                // version, every further video file next to it an additional
                // one (different qualities in the same folder). Formerly only
                // the largest remained, the second quality was dropped.
                var kandidaten = new List<(string Pfad, long Groesse)>();
                string? verweis = null;
                long verweisGroesse = -1;

                foreach (var datei in Dateien(ordner))
                {
                    var endung = Path.GetExtension(datei).ToLowerInvariant();
                    if (q.Art == Quellenart.Stream && endung != ".strm")
                    {
                        // In the stream source only references count; real copies
                        // would land twice in the target as a second variant.
                        continue;
                    }

                    if (!VideoEndungen.Contains(endung))
                    {
                        continue;
                    }

                    if (Textregeln.IstSample(datei) || IstBeilage(datei))
                    {
                        continue;
                    }

                    var groesse = Groesse(datei);

                    // NAS reference instead of a file: the movie arrives as a
                    // stream anyway. Only remember it as a fallback.
                    if (q.Art == Quellenart.Nas && endung == ".strm")
                    {
                        if (groesse > verweisGroesse)
                        {
                            verweisGroesse = groesse;
                            verweis = datei;
                        }

                        continue;
                    }

                    kandidaten.Add((datei, groesse));
                }

                // Largest first (= best quality), on ties by path: the number in
                // the file name must stay the same between two runs.
                kandidaten.Sort((a, b) => a.Groesse != b.Groesse
                    ? b.Groesse.CompareTo(a.Groesse)
                    : string.CompareOrdinal(a.Pfad, b.Pfad));

                var beste = kandidaten.Count > 0 ? kandidaten[0].Pfad : null;

                if (beste is null)
                {
                    if (verweis is not null)
                    {
                        notnagel.Add((name, ordner, q, verweis));
                    }

                    continue;
                }

                var tid = KennungFuer(name, ordner, index, zuordnungen);
                var schluessel = tid is null ? Textregeln.Norm(name) : "tmdb:" + tid;
                var anzeige = ZielName(tid, Textregeln.DisplayName(name, ordner), index, zusatz);

                if (!gruppen.TryGetValue(schluessel, out var g))
                {
                    g = new Gruppe { Schluessel = schluessel, Anzeigename = anzeige, TmdbId = tid };
                    gruppen[schluessel] = g;
                }
                else if (q.Rang < g.AnzeigeRang)
                {
                    // As with series: the source holding the original wins the
                    // display name. Stream sources often carry a wrong year.
                    g.Anzeigename = anzeige;
                }

                if (q.Rang < g.AnzeigeRang)
                {
                    g.AnzeigeRang = q.Rang;
                }

                if (!g.Beitraege.TryGetValue(q.Kennung, out var beitrag))
                {
                    beitrag = new Quellenbeitrag { Wurzel = q.Pfad, Ordner = ordner, Datei = beste };
                    g.Beitraege[q.Kennung] = beitrag;
                }
                // The same movie several times in the same source: take every
                // further file along, otherwise a medium is lost. The order is
                // provided by AlleDateien() (sorted), so that the number in the
                // target name stays stable.
                foreach (var (pfad, _) in kandidaten)
                {
                    if (!string.Equals(pfad, beitrag.Datei, StringComparison.Ordinal)
                        && !beitrag.Weitere.Contains(pfad, StringComparer.Ordinal))
                    {
                        beitrag.Weitere.Add(pfad);
                    }
                }
            }
        }

        // Fallback: only movies that would otherwise not appear at all.
        foreach (var (name, pfad, q, datei) in notnagel)
        {
            var tid = KennungFuer(name, pfad, index, zuordnungen);
            var schluessel = tid is null ? Textregeln.Norm(name) : "tmdb:" + tid;
            if (gruppen.ContainsKey(schluessel))
            {
                continue;
            }

            gruppen[schluessel] = new Gruppe
            {
                Schluessel = schluessel,
                TmdbId = tid,
                Anzeigename = ZielName(tid, Textregeln.DisplayName(name, pfad), index, zusatz),
                AnzeigeRang = q.Rang,
                NotnagelKennung = q.Kennung,
                Notnagel = new Quellenbeitrag { Wurzel = q.Pfad, Ordner = pfad, Datei = datei }
            };
        }

        return gruppen;
    }

    /// <summary>
    /// Series: one folder = one series, every file with SxxExx is an episode.
    /// </summary>
    public static Dictionary<string, Gruppe> Serien(
        IReadOnlyList<Quelle> quellen,
        TmdbIndex index,
        IReadOnlyDictionary<string, (string Titel, string? Jahr)> zusatz,
        ILogger log,
        IReadOnlyDictionary<string, Zuordnung>? zuordnungen = null)
    {
        var gruppen = new Dictionary<string, Gruppe>(StringComparer.Ordinal);
        var notnagelSerien = new List<(string Schluessel, string Kennung, Quellenbeitrag Beitrag)>();

        foreach (var q in quellen)
        {
            if (!q.Erreichbar())
            {
                log.LogWarning("Source {Kennung}: {Pfad} not reachable, skipped", q.Kennung, q.Pfad);
                continue;
            }

            string[] eintraege;
            try
            {
                eintraege = Directory.GetDirectories(q.Pfad);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Source {Kennung}: {Pfad} not readable ({Fehler})", q.Kennung, q.Pfad, ex.Message);
                continue;
            }

            foreach (var ordner in eintraege.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(ordner);
                if (name.StartsWith('.') || string.Equals(name, "_sicherung", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hand = Handzuordnung(ordner, zuordnungen);
                var schluessel = hand is null ? Textregeln.SerienKey(name) : (Textregeln.IstFremd(hand) ? hand : "tmdb:" + hand);
                if (!gruppen.TryGetValue(schluessel, out var g))
                {
                    g = new Gruppe
                    {
                        Schluessel = schluessel,
                        TmdbId = hand ?? Textregeln.TmdbAusNamen(name),
                        Anzeigename = ZielName(hand, Textregeln.DisplayName(name, ordner), index, zusatz)
                    };
                    gruppen[schluessel] = g;
                }
                else if (q.Rang < g.AnzeigeRang)
                {
                    g.Anzeigename = Textregeln.DisplayName(name, ordner);
                }

                if (q.Rang < g.AnzeigeRang)
                {
                    g.AnzeigeRang = q.Rang;
                }

                // Pull in the year if the own name carries none
                if (!Regex.IsMatch(g.Anzeigename, @"\(\d{4}\)"))
                {
                    var jahr = Textregeln.SerienJahr(name);
                    if (jahr is not null)
                    {
                        var basis = Regex.Replace(g.Anzeigename, @"\s*\(\d{4}\)", string.Empty).Trim();
                        g.Anzeigename = $"{basis} ({jahr})";
                    }
                }

                var folgen = new Dictionary<(int Staffel, int Folge), List<string>>();
                var verweise = new Dictionary<(int Staffel, int Folge), List<string>>();

                foreach (var datei in Dateien(ordner))
                {
                    var endung = Path.GetExtension(datei).ToLowerInvariant();
                    if (!VideoEndungen.Contains(endung) || Textregeln.IstSample(datei))
                    {
                        continue;
                    }

                    var sk = Textregeln.SeKey(Path.GetFileName(datei));
                    if (sk is null)
                    {
                        continue;
                    }

                    var fach = (sk.Value.Staffel, sk.Value.Folgen[0]);
                    if (q.Art == Quellenart.Nas && endung == ".strm")
                    {
                        if (!verweise.TryGetValue(fach, out var vliste))
                        {
                            vliste = new List<string>();
                            verweise[fach] = vliste;
                        }

                        vliste.Add(datei);
                        continue;
                    }

                    if (!folgen.TryGetValue(fach, out var liste))
                    {
                        liste = new List<string>();
                        folgen[fach] = liste;
                    }

                    liste.Add(datei);
                }

                if (folgen.Count > 0)
                {
                    g.Beitraege[q.Kennung] = new Quellenbeitrag { Wurzel = q.Pfad, Ordner = ordner, Folgen = folgen };
                }
                else if (verweise.Count > 0 && !g.Beitraege.ContainsKey(q.Kennung))
                {
                    // Only noted for now: the reference counts only when the
                    // series exists as a real file in no source. Otherwise the
                    // NAS store would hang every episode a second time into the
                    // target tree.
                    notnagelSerien.Add((schluessel, q.Kennung, new Quellenbeitrag
                    {
                        Wurzel = q.Pfad,
                        Ordner = ordner,
                        Folgen = verweise
                    }));
                }
            }
        }

        // Pull in references: only for series that would otherwise not appear.
        foreach (var (schluessel, kennung, beitrag) in notnagelSerien)
        {
            if (gruppen.TryGetValue(schluessel, out var g) && g.Beitraege.Count == 0)
            {
                g.Beitraege[kennung] = beitrag;
            }
        }

        // Final target name: the rule beats the source priority.
        foreach (var g in gruppen.Values)
        {
            var tid = g.TmdbId ?? index.IdFuer(g.Anzeigename, Textregeln.DisplayYear(g.Anzeigename));
            if (tid is not null)
            {
                g.TmdbId = tid;
                g.Anzeigename = ZielName(tid, g.Anzeigename, index, zusatz);
            }
        }

        return gruppen;
    }

    /// <summary>
    /// Target name according to the rule 'Title (Year) [tmdbid-123]'. If the
    /// data is missing, the previous name stays - a wrong title is worse
    /// than an imperfect one.
    /// </summary>
    public static string ZielName(
        string? tmdbId,
        string ersatz,
        TmdbIndex index,
        IReadOnlyDictionary<string, (string Titel, string? Jahr)> zusatz)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return ersatz;
        }

        // Foreign tag (tvdb:407966): no TMDB lookup possible, title and year
        // come from the manual mapping in zusatz. The tag stays in the name so
        // that the mapping remains visible in the tree and is preserved.
        if (Textregeln.IstFremd(tmdbId))
        {
            var teile = tmdbId.Split(':', 2);
            var art = teile[0];
            var nummer = teile[1];
            var titelFremd = Textregeln.NameSaeubern(Textregeln.TitelOhneAnhang(
                zusatz.TryGetValue(tmdbId, out var fremd) ? fremd.Titel : ersatz));
            if (titelFremd.Length == 0)
            {
                return ersatz;
            }

            var jahrFremd = zusatz.TryGetValue(tmdbId, out var fremdJahr) ? fremdJahr.Jahr : null;
            return string.IsNullOrWhiteSpace(jahrFremd)
                ? $"{titelFremd} [{art}id-{nummer}]"
                : $"{titelFremd} ({jahrFremd}) [{art}id-{nummer}]";
        }

        (string Titel, string? Jahr)? info = null;
        if (zusatz.TryGetValue(tmdbId, out var z))
        {
            info = z;
        }
        else
        {
            info = index.Info(tmdbId);
        }

        if (info is null || string.IsNullOrWhiteSpace(info.Value.Titel))
        {
            return ersatz;
        }

        // The stored name may already contain year and tag - otherwise an
        // appendage grows with every run.
        var titel = Textregeln.NameSaeubern(Textregeln.TitelOhneAnhang(info.Value.Titel));
        if (titel.Length == 0)
        {
            return ersatz;
        }

        if (info.Value.Jahr is null)
        {
            return $"{titel} [tmdbid-{tmdbId}]";
        }

        // If the tag stands at the end and is four digits (1930), Jellyfin
        // reads it as a year. Then it goes before the year - the last
        // year-capable word in the name is the right one that way.
        if (Textregeln.JahresFalle(tmdbId))
        {
            return $"{titel} [tmdbid-{tmdbId}] ({info.Value.Jahr})";
        }

        return $"{titel} ({info.Value.Jahr}) [tmdbid-{tmdbId}]";
    }

    /// <summary>"...-trailer", "...-sample": Jellyfin does not count these as a version.</summary>
    private static readonly string[] Beilagen =
    {
        "-trailer", "-sample", "-scene", "-interview", "-behindthescenes",
        "-deleted", "-deletedscene", "-featurette", "-short", "-other",
        "-commentary", "-bonus"
    };

    private static bool IstBeilage(string datei)
    {
        var stamm = Path.GetFileNameWithoutExtension(datei).Trim().ToLowerInvariant();
        if (stamm is "sample" or "trailer" or "extras")
        {
            return true;
        }

        return Beilagen.Any(b => stamm.EndsWith(b, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Dateien(string ordner)
    {
        try
        {
            return Directory.EnumerateFiles(ordner, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static long Groesse(string pfad)
    {
        try
        {
            return new FileInfo(pfad).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// TMDB id of a folder: name, then NFO, then a unique id from the file
    /// names. Several different ids = unclear case (then none), so that
    /// nothing merges wrongly.
    /// </summary>
    private static string? KennungFuer(
        string name,
        string ordner,
        TmdbIndex index,
        IReadOnlyDictionary<string, Zuordnung>? zuordnungen)
    {
        var hand = Handzuordnung(ordner, zuordnungen);
        if (hand is not null)
        {
            return hand;
        }

        var tid = Textregeln.TmdbAusNamen(name);
        if (tid is not null)
        {
            return tid;
        }

        tid = Nfo.TmdbId(ordner);
        if (tid is not null)
        {
            return tid;
        }

        var gefunden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var datei in Dateien(ordner))
        {
            var k = Textregeln.TmdbAusNamen(Path.GetFileName(datei));
            if (k is not null)
            {
                gefunden.Add(k);
            }
        }

        if (gefunden.Count == 1)
        {
            return gefunden.First();
        }

        // Neither name nor NFO carry an id: search by title and year in the
        // inventory that Jellyfin already knows.
        var anzeige = Textregeln.DisplayName(name, ordner);
        return index.IdFuer(anzeige, Textregeln.DisplayYear(anzeige));
    }

    /// <summary>Manual mapping of a source folder from the stored map.</summary>
    private static string? Handzuordnung(string ordner, IReadOnlyDictionary<string, Zuordnung>? zuordnungen)
    {
        if (zuordnungen is null || string.IsNullOrWhiteSpace(ordner))
        {
            return null;
        }

        if (zuordnungen.TryGetValue(ordner, out var direkt) && Textregeln.Kennung(direkt) is { } direktTag)
        {
            return direktTag;
        }

        // The inventory mixes full paths and bare folder names - both must
        // match, otherwise a manual mapping has no effect.
        var name = Path.GetFileName(ordner.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var (schluessel, eintrag) in zuordnungen)
        {
            if (Textregeln.Kennung(eintrag) is not { } eintragTag)
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(schluessel.TrimEnd('/', '\\')), name, StringComparison.OrdinalIgnoreCase))
            {
                return eintragTag;
            }
        }

        return null;
    }
}
