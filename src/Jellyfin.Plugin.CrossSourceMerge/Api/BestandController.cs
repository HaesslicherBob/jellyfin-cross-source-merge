using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CrossSourceMerge.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Api;

/// <summary>
/// List of source folders with filter, sorting and pages. One row per
/// source folder - the UI shows "Missing mappings" from it and
/// lets a provider and an id be set per row.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/CrossSourceMerge/Bestand")]
public class BestandController : ControllerBase
{
    private readonly ILibraryManager _bibliothek;
    private readonly IApplicationPaths _pfade;
    private readonly ILogger<BestandController> _log;

    /// <summary>New controller.</summary>
    public BestandController(ILibraryManager bibliothek, IApplicationPaths pfade, ILogger<BestandController> log)
    {
        _bibliothek = bibliothek;
        _pfade = pfade;
        _log = log;
    }

    private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string Zustandsdatei => Path.Combine(_pfade.PluginConfigurationsPath, "crosssource-zustand.json");

    /// <summary>One page of the inventory list.</summary>
    [HttpGet]
    public ActionResult<object> Liste(
        [FromQuery] string? art,
        [FromQuery] string? status,
        [FromQuery] string? suche,
        [FromQuery] string? kategorie,
        [FromQuery] string? quelle,
        [FromQuery] string? sort,
        [FromQuery] string? richtung,
        [FromQuery] int seite = 1,
        [FromQuery] int groesse = 50,
        [FromQuery] bool erneuern = false)
    {
        if (erneuern)
        {
            Bestand.Verwerfen();
        }

        var serie = string.Equals(art, "series", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(art, "tv", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(art, "serien", StringComparison.OrdinalIgnoreCase);
        var cfg = Config;
        var zustand = Zustand.Laden(Zustandsdatei);
        var index = AusBibliothek();

        // "alle" means: movies and series in one list. Each row carries
        // its kind itself, so the UI no longer has to
        // filter by it.
        var alles = string.Equals(art, "alle", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<Bestand.Zeile> alle;
        int ohneMedium;
        if (alles)
        {
            var filme = Bestand.Ermitteln(cfg, false, index, zustand, _log);
            var serien = Bestand.Ermitteln(cfg, true, index, zustand, _log);
            alle = filme.Zeilen.Concat(serien.Zeilen).ToList();
            ohneMedium = filme.OhneMedium + serien.OhneMedium;
        }
        else
        {
            var ergebnis = Bestand.Ermitteln(cfg, serie, index, zustand, _log);
            alle = ergebnis.Zeilen;
            ohneMedium = ergebnis.OhneMedium;
        }

        var offen = alle.Count(z => z.Status != "zugeordnet");

        IEnumerable<Bestand.Zeile> gefiltert = alle;
        var wunsch = (status ?? "offen").Trim().ToLowerInvariant();
        gefiltert = wunsch switch
        {
            "alle" => gefiltert,
            "zugeordnet" => gefiltert.Where(z => z.Status == "zugeordnet"),
            "ohne-kennung" => gefiltert.Where(z => z.Status == "ohne-kennung"),
            "ohne-info" => gefiltert.Where(z => z.OhneInfo),
            _ => gefiltert.Where(z => z.Status != "zugeordnet")
        };

        if (!string.IsNullOrWhiteSpace(kategorie))
        {
            var wunschKat = kategorie.Trim();
            gefiltert = string.Equals(wunschKat, "standard", StringComparison.OrdinalIgnoreCase)
                ? gefiltert.Where(z => z.Kategorien.Count == 0)
                : gefiltert.Where(z => z.Kategorien.Contains(wunschKat, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(quelle) && !string.Equals(quelle, "alle", StringComparison.OrdinalIgnoreCase))
        {
            var wunschQuelle = quelle.Trim();
            gefiltert = gefiltert.Where(z => z.Quelle.Contains(wunschQuelle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(suche))
        {
            var muster = suche.Trim();
            gefiltert = gefiltert.Where(z =>
                z.Ordner.Contains(muster, StringComparison.OrdinalIgnoreCase)
                || z.IstName.Contains(muster, StringComparison.OrdinalIgnoreCase)
                || z.Quelle.Contains(muster, StringComparison.OrdinalIgnoreCase)
                || (z.IstId ?? string.Empty).Contains(muster, StringComparison.OrdinalIgnoreCase));
        }

        var liste = gefiltert.ToList();
        var schluessel = (sort ?? "name").Trim().ToLowerInvariant();
        var ab = string.Equals(richtung, "ab", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(richtung, "desc", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(richtung, "descending", StringComparison.OrdinalIgnoreCase);

        // Empty values always go last - also with descending sorting.
        var mitWert = liste.Where(z => Wert(z, schluessel) is not null).ToList();
        var ohneWert = liste.Where(z => Wert(z, schluessel) is null).ToList();
        var sortiert = mitWert
            .OrderBy(z => Wert(z, schluessel), StringComparer.OrdinalIgnoreCase)
            .ThenBy(z => z.Ordner, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ab)
        {
            sortiert.Reverse();
        }

        sortiert.AddRange(ohneWert.OrderBy(z => z.Ordner, StringComparer.OrdinalIgnoreCase));

        var proSeite = Math.Clamp(groesse, 10, 500);
        var seiten = Math.Max(1, (int)Math.Ceiling(sortiert.Count / (double)proSeite));
        var aktuelle = Math.Clamp(seite, 1, seiten);
        var teil = sortiert.Skip((aktuelle - 1) * proSeite).Take(proSeite)
            .Select(z => new
            {
                art = z.Art,
                quelle = z.Quelle,
                ordner = z.Ordner,
                pfad = z.Pfad,
                datei = z.Datei,
                istId = z.IstId,
                istHerkunft = z.IstHerkunft,
                istName = z.IstName,
                istJahr = z.IstJahr,
                handAnbieter = z.HandAnbieter,
                handKennung = z.HandKennung,
                handName = z.HandName,
                handJahr = z.HandJahr,
                ziel = z.Ziel,
                status = z.Status,
                ohneInfo = z.OhneInfo,
                fehlt = z.Fehlt,
                kategorien = z.Kategorien
            })
            .ToList();

        return Ok(new
        {
            gesamt = alle.Count,
            offen,
            gefiltert = sortiert.Count,
            seite = aktuelle,
            seiten,
            groesse = proSeite,
            sort = schluessel,
            richtung = ab ? "ab" : "auf",
            ohneMedium,
            kategorien = Kategorien.Liste(cfg.Kategorien, serie).Select(k => k.Name).ToList(),

            // Categories of the other kind: selectable in the title list, so
            // a movie can also be filed under "Dokumentationen" or into a
            // series tree. The UI shows them separately.
            fremdeKategorien = cfg.Kategorien
                .Where(k => k.IstSerie != serie && !string.IsNullOrWhiteSpace(k.Zielpfad))
                .Select(k => k.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            quellen = cfg.Quellen.Select(q => q.Kennung).ToList(),
            standardKategorie = Kategorien.Standard(cfg.Kategorien, serie)?.Name,
            tmdbSchluessel = !string.IsNullOrWhiteSpace(cfg.TmdbSchluessel),
            omdbSchluessel = !string.IsNullOrWhiteSpace(cfg.OmdbSchluessel),
            zeilen = teil
        });
    }

    private static string? Wert(Bestand.Zeile z, string schluessel) => schluessel switch
    {
        "quelle" => z.Quelle,
        "jahr" => z.IstJahr?.ToString(CultureInfo.InvariantCulture),
        "id" => z.IstId,
        "ziel" => z.Ziel,
        "status" => z.Status,
        _ => string.IsNullOrWhiteSpace(z.IstName) ? null : z.IstName
    };

    private TmdbIndex AusBibliothek()
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

        return TmdbIndex.AusBestand(bestand);
    }
}
