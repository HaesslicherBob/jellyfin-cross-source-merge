using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>One complete merge run: read, plan, write.</summary>
public sealed class MergeLauf
{
    private static readonly SemaphoreSlim Sperre = new(1, 1);
    private readonly ILibraryManager _bibliothek;
    private readonly IApplicationPaths _pfade;
    private readonly ILogger _log;

    /// <summary>New run.</summary>
    public MergeLauf(ILibraryManager bibliothek, IApplicationPaths pfade, ILogger log)
    {
        _bibliothek = bibliothek;
        _pfade = pfade;
        _log = log;
    }

    /// <summary>Path of the state file (manifest, mappings, categories).</summary>
    public string Zustandsdatei => Path.Combine(_pfade.PluginConfigurationsPath, "crosssource-zustand.json");

    /// <summary>Path of the expected list (what the last run created).</summary>
    public string Solldatei => Path.Combine(_pfade.PluginConfigurationsPath, "crosssource-soll.json");

    /// <summary>
    /// Runs the merge for the configured categories.
    /// </summary>
    public async Task<Laufbericht> AusfuehrenAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var bericht = new Laufbericht();
        var verweiseGeprueft = new HashSet<string>(StringComparer.Ordinal);
        var soll = new Sollstand { Stand = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };

        Kategorien.Auffuellen(cfg.Kategorien, cfg.ZielFilme, cfg.ZielSerien);
        if (cfg.Kategorien.Count == 0)
        {
            throw new InvalidOperationException(
                "No category configured - please enter a target folder under Plugins > Cross-Source Merge.");
        }

        await Sperre.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var zustand = Zustand.Laden(Zustandsdatei);
            var index = AusBibliothek(cfg);
            var zusatz = ZusatzAusZuordnungen(zustand);
            var ffmpeg = Schatten.Ffmpeg(null);
            var ffprobe = MediaInfo.Ffprobe(ffmpeg);

            var alleQuellen = Quellen(cfg, istSerie: false)
                .Concat(Quellen(cfg, istSerie: true))
                .ToList();
            var tot = Schatten.ToteWurzeln(alleQuellen, _log);

            foreach (var istSerie in new[] { false, true })
            {
                var quellen = Quellen(cfg, istSerie);
                var kategorien = Kategorien.Liste(cfg.Kategorien, istSerie);

                // The category carries the art: a film that is really a series
                // gets a series category and is then written as a series into
                // that tree (season folder, S01E01). Without this the work would
                // sit in the list, mapped, and nothing would happen - the
                // series categories were simply not looked at in the film pass.
                if (!istSerie)
                {
                    kategorien = kategorien
                        .Concat(Kategorien.Liste(cfg.Kategorien, true))
                        .ToList();
                }

                if (quellen.Count == 0 || kategorien.Count == 0)
                {
                    continue;
                }

                var gruppen = istSerie
                    ? Scannen.Serien(quellen, index, zusatz, _log, zustand.Zuordnungen)
                    : Scannen.Filme(quellen, index, zusatz, _log, zustand.Zuordnungen);

                await ErgaenzenAsync(gruppen, index, cfg, istSerie, ct).ConfigureAwait(false);

                var merkArt = istSerie ? "series" : "movie";
                foreach (var g in gruppen.Values)
                {
                    var k = Zustand.Schluessel(merkArt, g.Schluessel);
                    if (!zustand.Namen.ContainsKey(k) && !string.IsNullOrWhiteSpace(g.Anzeigename))
                    {
                        zustand.Namen[k] = Textregeln.NameNormalisieren(g.Anzeigename);
                    }
                }

                var verteilung = Kategorien.Aufloesen(cfg.Kategorien, istSerie, gruppen, zustand, cfg.Quellen);
                var ersatz = istSerie ? new Ersatzindex(gruppen, quellen) : null;
                var art = istSerie ? "Series" : "Movies";

                bericht.Werke += gruppen.Count;

                foreach (var kat in kategorien)
                {
                    var schluessel = verteilung
                        .Where(kv => kv.Value.Contains(kat.Name))
                        .Select(kv => kv.Key)
                        .ToHashSet(StringComparer.Ordinal);

                    if (schluessel.Count == 0)
                    {
                        continue;
                    }

                    // From here on the category decides, not the pass: a title in
                    // a foreign tree is planned, named and noted the way that
                    // tree is built.
                    var katSerie = kat.IstSerie;
                    var plan = katSerie
                        ? Planen.Serien(gruppen, kat.Zielpfad, quellen, schluessel, folgenErfinden: !istSerie)
                        : Planen.Filme(gruppen, kat.Zielpfad, quellen, schluessel);

                    // Entries whose source is currently gone stay in place.
                    var schatten = Schatten.Pfade(kat.Zielpfad, katSerie, tot, _log);
                    foreach (var pfad in schatten.Keys)
                    {
                        plan.Add(new PlanEintrag { Ziel = pfad, Quelle = null, IstSerie = katSerie });
                    }

                    if (!cfg.NurPlanen)
                    {
                        Merken(plan, quellen, zustand, katSerie, ffprobe);
                        PruefeZiel(kat.Zielpfad, verweiseGeprueft);
                    }

                    bericht.Eintraege += plan.Count;
                    _log.LogInformation(
                        "{Art} / {Kategorie}: {Werke} titles, {Eintraege} entries ({Schatten} shadow)",
                        art,
                        kat.Name,
                        schluessel.Count,
                        plan.Count,
                        schatten.Count);

                    Anwenden.Schreiben(plan, cfg.NurPlanen, bericht, _log, zustand, tot, ersatz, ffmpeg);

                    if (!cfg.NurPlanen && cfg.NfoSchreiben)
                    {
                        SchreibNfo(plan, gruppen, katSerie, cfg.NfoJahrKorrigieren, bericht);
                    }

                    if (!cfg.NurPlanen)
                    {
                        // What counts is what the plan really creates - not the
                        // display name, which can differ without a tag.
                        var sollOrdner = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var e in plan)
                        {
                            if (e.Quelle is null)
                            {
                                continue;
                            }

                            var ordnerPfad = katSerie
                                ? Path.GetDirectoryName(Path.GetDirectoryName(e.Ziel))
                                : Path.GetDirectoryName(e.Ziel);
                            if (ordnerPfad is not null)
                            {
                                sollOrdner.Add(Path.GetFileName(ordnerPfad));
                            }
                        }
                        foreach (var pfad in schatten.Keys)
                        {
                            var rel = Path.GetRelativePath(kat.Zielpfad, pfad);
                            var oben = rel.Split(Path.DirectorySeparatorChar)[0];
                            if (oben.Length > 0)
                            {
                                sollOrdner.Add(oben);
                            }
                        }

                        var sollDateien = new HashSet<string>(
                            plan.Where(p => p.Quelle is not null).Select(p => Path.GetFullPath(p.Ziel)),
                            StringComparer.Ordinal);
                        if (soll.Kategorien.TryGetValue(kat.Zielpfad, out var sollKat))
                        {
                            sollKat.Ordner.AddRange(sollOrdner);
                            sollKat.Dateien.AddRange(sollDateien);
                        }
                        else
                        {
                            soll.Kategorien[kat.Zielpfad] = new Sollkategorie
                            {
                                Ordner = sollOrdner.ToList(),
                                Dateien = sollDateien.ToList()
                            };
                        }

                        if (cfg.Aufraeumen && tot.Count == 0)
                        {
                            Aufraeumen.Altes(kat.Zielpfad, sollOrdner, sollDateien, cfg.NurPlanen, _log, cfg.AufraeumenTage);
                        }
                        else if (cfg.Aufraeumen)
                        {
                            _log.LogInformation(
                                "Cross-Source Merge: cleanup suspended because {Anzahl} source(s) are missing",
                                tot.Count);
                        }
                    }
                }
            }

            if (tot.Count > 0)
            {
                // If a source were gone now, its titles would not be in the plan -
                // the expected list would report them as orphaned. Keep the old version.
                _log.LogInformation(
                    "Cross-Source Merge: expected list stays unchanged, {Anzahl} source(s) are missing", tot.Count);
            }

            if (!cfg.NurPlanen && tot.Count == 0 && soll.Kategorien.Count > 0)
            {
                try
                {
                    soll.Speichern(Solldatei);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning("Expected list could not be written: {Fehler}", ex.Message);
                }
            }

            if (!cfg.NurPlanen)
            {
                try
                {
                    zustand.Speichern();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning("State could not be saved: {Fehler}", ex.Message);
                }
            }

            if (bericht.Gruende.Count > 0)
            {
                _log.LogInformation("Set by reason: {Gruende}",
                    string.Join(", ", bericht.Gruende.Select(kv => kv.Key + "=" + kv.Value)));
            }

            Melden(bericht, cfg.NurPlanen);
            return bericht;
        }
        finally
        {
            Sperre.Release();
        }
    }

    /// <summary>Create missing NFO files per title folder.</summary>
    private void SchreibNfo(
        IReadOnlyList<PlanEintrag> plan,
        IReadOnlyDictionary<string, Gruppe> gruppen,
        bool istSerie,
        bool jahrKorrigieren,
        Laufbericht bericht)
    {
        var ordner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in plan)
        {
            if (e.Quelle is null || string.IsNullOrEmpty(e.WerkKennung))
            {
                continue;
            }

            var pfad = istSerie ? Path.GetDirectoryName(Path.GetDirectoryName(e.Ziel)) : Path.GetDirectoryName(e.Ziel);
            if (pfad is not null)
            {
                ordner[e.WerkKennung] = pfad;
            }
        }

        foreach (var (schluessel, pfad) in ordner)
        {
            if (!gruppen.TryGetValue(schluessel, out var g))
            {
                continue;
            }

            if (Metadaten.SchreibeNfo(pfad, istSerie, g.Anzeigename, g.TmdbId, _log))
            {
                bericht.Gruende["nfo"] = bericht.Gruende.TryGetValue("nfo", out var n) ? n + 1 : 1;
            }
            else if (jahrKorrigieren &&
                     Metadaten.JahrKorrigieren(pfad, istSerie, g.Anzeigename, g.TmdbId, _log))
            {
                bericht.Gruende["nfo-jahr"] = bericht.Gruende.TryGetValue("nfo-jahr", out var j) ? j + 1 : 1;
            }
        }
    }

    private void Melden(Laufbericht bericht, bool nurPlanen)
    {
        if (nurPlanen)
        {
            _log.LogInformation(
                "Dry run finished: {Werke} titles, {Eintraege} entries, of which {Geplant} writable, "
                + "{Schatten} shadow, {Stehen} without source, {Fehler} errors - nothing was written",
                bericht.Werke,
                bericht.Eintraege,
                bericht.Geplant,
                bericht.Schatten,
                bericht.StehenGelassen,
                bericht.FehlerZahl);
        }
        else
        {
            _log.LogInformation(
                "Done: {Werke} titles, {Eintraege} entries, {Gesetzt} set, {Unveraendert} unchanged, "
                + "{Schatten} shadow, {Attrappen} placeholders, {Ersatz} substitutes, {Fehler} errors",
                bericht.Werke,
                bericht.Eintraege,
                bericht.Gesetzt,
                bericht.Unveraendert,
                bericht.Schatten,
                bericht.Attrappen,
                bericht.Ersatz,
                bericht.FehlerZahl);
        }
    }

    /// <summary>
    /// Before the first write, check whether the target folder accepts
    /// links at all. Otherwise the run writes 80 000 error lines instead of
    /// saying once what is missing.
    /// </summary>
    private void PruefeZiel(string ziel, HashSet<string> geprueft)
    {
        if (!geprueft.Add(ziel))
        {
            return;
        }

        if (!Betriebssystem.VerweiseMoeglich(ziel, out var grund))
        {
            throw new InvalidOperationException(
                "The target folder " + ziel + " does not accept symbolic links: " + grund
                + ". The merge needs links (no copies) - please choose a folder on a local "
                + "file system, not an SMB/NTFS share without link permission.");
        }

        if (Betriebssystem.IgnoriertGrossKleinschreibung(ziel))
        {
            _log.LogWarning(
                "Cross-Source Merge: the target folder {Ziel} does not distinguish upper and lower case - "
                + "two titles that differ only in that share one folder.",
                ziel);
        }
    }

    /// <summary>
    /// Record size and video data of the NAS files - a placeholder in
    /// original quality is later built from it.
    /// </summary>
    private void Merken(
        IReadOnlyList<PlanEintrag> plan,
        IReadOnlyList<Quelle> quellen,
        Zustand zustand,
        bool istSerie,
        string? ffprobe)
    {
        var nas = quellen
            .Where(q => q.Art == Quellenart.Nas && Schatten.Erreichbar(q.Pfad))
            .Select(q => q.Pfad.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();
        if (nas.Count == 0)
        {
            return;
        }

        var art = istSerie ? "series" : "movie";
        var gesehen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var eintrag in plan)
        {
            if (eintrag.Quelle is null || !nas.Any(n => eintrag.Quelle!.StartsWith(n, StringComparison.Ordinal)))
            {
                continue;
            }

            // Only real video files qualify as a model for a placeholder;
            // a .strm carries no video data.
            var endung = Path.GetExtension(eintrag.Quelle).ToLowerInvariant();
            if (!Scannen.VideoEndungen.Contains(endung))
            {
                continue;
            }

            var schluessel = Zustand.Schluessel(art, eintrag.WerkKennung);
            if (!gesehen.Add(schluessel))
            {
                continue;
            }

            long groesse;
            try
            {
                groesse = new FileInfo(eintrag.Quelle).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Placeholders from early runs are tiny and do not qualify
            // as a model for a placeholder.
            if (groesse < 1_000_000)
            {
                continue;
            }

            if (zustand.Manifest.TryGetValue(schluessel, out var vorhanden) && vorhanden.Groesse == groesse)
            {
                continue;
            }

            var (dauer, breite, hoehe) = MediaInfo.Erheben(eintrag.Quelle, ffprobe);
            zustand.Merken(art, eintrag.WerkKennung, groesse, breite, hoehe, dauer, eintrag.Quelle);
        }
    }

    /// <summary>
    /// Sources of one media type from the configuration, sorted by rank.
    /// </summary>
    public static List<Quelle> Quellen(PluginConfiguration cfg, bool istSerie)
    {
        var liste = new List<Quelle>();
        foreach (var q in cfg.Quellen)
        {
            // A regular row carries one folder; a legacy row still two.
            var pfad = string.IsNullOrWhiteSpace(q.Pfad)
                ? (istSerie ? q.SerienPfad : q.FilmePfad)
                : q.Pfad;
            if (string.IsNullOrWhiteSpace(pfad))
            {
                continue;
            }

            // Which media type the source delivers is stated by its default
            // category. Without a category the source applies to both types.
            var medienart = Kategorien.Art(cfg.Kategorien, q.StandardKategorie);
            if (medienart.HasValue && medienart.Value != istSerie)
            {
                continue;
            }

            liste.Add(new Quelle
            {
                Kennung = string.IsNullOrWhiteSpace(q.Kennung) ? Path.GetFileName(pfad.TrimEnd('/')) : q.Kennung,
                Art = q.Art,
                Rang = q.Rang,
                Pfad = pfad,
                Schatten = q.Schatten
            });
        }

        return liste.OrderBy(q => q.Rang).ThenBy(q => q.Kennung, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Title and year from memory: first the once chosen name,
    /// then the manual mapping. That way a folder keeps the name it has.
    /// </summary>
    private static Dictionary<string, (string, string?)> ZusatzAusZuordnungen(Zustand zustand)
    {
        var zusatz = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);

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
            if (!string.IsNullOrWhiteSpace(z.TmdbId) && !string.IsNullOrWhiteSpace(z.Name))
            {
                if (Textregeln.Kennung(z) is { } tag)
                {
                    zusatz[tag] = (z.Name!, z.Jahr?.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        return zusatz;
    }

    /// <summary>
    /// Title and year from the Jellyfin library. That way the plugin needs
    /// neither an external service nor a key.
    /// </summary>
    private TmdbIndex AusBibliothek(PluginConfiguration cfg)
    {
        var bestand = new List<(string Id, string Titel, int? Jahr)>();
        try
        {
            var abfrage = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            };

            foreach (var item in _bibliothek.GetItemList(abfrage))
            {
                if (!item.ProviderIds.TryGetValue("Tmdb", out var tid) || string.IsNullOrWhiteSpace(tid))
                {
                    continue;
                }

                bestand.Add((tid, item.Name, item.ProductionYear));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Library not readable, working without title index: {Fehler}", ex.Message);
        }

        var index = TmdbIndex.AusBestand(bestand);
        _log.LogInformation("Title index: {Anzahl} ids from the library", index.Anzahl);
        return index;
    }

    /// <summary>
    /// Look up titles without an id - only if a TMDB key
    /// is stored. Every hit must match the year.
    /// </summary>
    private async Task ErgaenzenAsync(
        Dictionary<string, Gruppe> gruppen,
        TmdbIndex index,
        PluginConfiguration cfg,
        bool istSerie,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.TmdbSchluessel))
        {
            return;
        }

        var suche = new TmdbIndex.Suche(new HttpClient(), cfg.TmdbSchluessel.Trim(), Sprache.Wert(cfg.TmdbSprache));
        var offen = gruppen.Values.Where(g => g.TmdbId is null).ToList();
        foreach (var g in offen)
        {
            var jahr = Textregeln.DisplayYear(g.Anzeigename);
            var treffer = await suche.FindeAsync(g.Anzeigename, jahr, istSerie, ct).ConfigureAwait(false);
            if (treffer is null)
            {
                continue;
            }

            var (id, titel, jahrTreffer) = treffer.Value;
            var bekanntesJahr = jahrTreffer is null ? (int?)null : int.Parse(jahrTreffer, CultureInfo.InvariantCulture);
            index.Merken(id, titel, bekanntesJahr);

            g.TmdbId = id;
            g.Anzeigename = Scannen.ZielName(id, g.Anzeigename, index, new Dictionary<string, (string, string?)>());
        }

        _log.LogInformation("TMDB search: {Abfragen} requests for {Offen} open titles", suche.Abfragen, offen.Count);
    }
}
