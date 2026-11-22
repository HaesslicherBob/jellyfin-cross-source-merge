using System.Globalization;
using Jellyfin.Plugin.CrossSourceMerge.Engine;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CrossSourceMerge.Api;

/// <summary>
/// Manual ids: check, save, delete. The mapping is attached to the
/// source folder, because that is where the name the merge reads lives.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/CrossSourceMerge")]
public class KennungController : ControllerBase
{
    private readonly IApplicationPaths _pfade;
    private readonly Tasks.MergeTask _aufgabe;
    private readonly ILogger<KennungController> _log;

    /// <summary>New controller.</summary>
    public KennungController(IApplicationPaths pfade, Tasks.MergeTask aufgabe, ILogger<KennungController> log)
    {
        _pfade = pfade;
        _aufgabe = aufgabe;
        _log = log;
    }

    private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string Zustandsdatei => Path.Combine(_pfade.PluginConfigurationsPath, "crosssource-zustand.json");

    /// <summary>Checks an id against TMDB (IMDB/TVDB are resolved).</summary>
    [HttpGet("Kennung")]
    public async Task<ActionResult<object>> Pruefen(
        [FromQuery] string? anbieter,
        [FromQuery] string? kennung,
        [FromQuery] bool serie,
        CancellationToken ct)
    {
        var cfg = Config;
        var treffer = await Kennungen.PruefenAsync(anbieter ?? "tmdb", kennung ?? string.Empty, serie, cfg.TmdbSchluessel, Engine.Sprache.Wert(cfg.TmdbSprache), ct, cfg.OmdbSchluessel, HttpContext.RequestServices)
            .ConfigureAwait(false);
        return Ok(new
        {
            gefunden = treffer.Gefunden,
            titel = treffer.Titel,
            jahr = treffer.Jahr,
            tmdbId = treffer.TmdbId,
            anbieter = treffer.Anbieter,
            kennung = treffer.Kennung,
            hinweis = treffer.Hinweis
        });
    }

    /// <summary>Searches for an id for a title.</summary>
    [HttpGet("Kennung/Suche")]
    public async Task<ActionResult<object>> Suchen(
        [FromQuery] string? name,
        [FromQuery] string? jahr,
        [FromQuery] bool serie,
        CancellationToken ct)
    {
        var cfg = Config;
        var treffer = await Kennungen.SuchenAsync(name ?? string.Empty, jahr, serie, cfg.TmdbSchluessel, Engine.Sprache.Wert(cfg.TmdbSprache), ct)
            .ConfigureAwait(false);

        // The year in the folder name often differs (German release, special).
        // So ask a second time without the year before reporting "no match".
        if (!treffer.Gefunden && !string.IsNullOrWhiteSpace(jahr))
        {
            var ohneJahr = await Kennungen.SuchenAsync(name ?? string.Empty, null, serie, cfg.TmdbSchluessel, Engine.Sprache.Wert(cfg.TmdbSprache), ct)
                .ConfigureAwait(false);
            if (ohneJahr.Gefunden)
            {
                var gefundenesJahr = ohneJahr.Jahr is null
                    ? "without year"
                    : ohneJahr.Jahr.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                treffer = ohneJahr with
                {
                    Hinweis = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Year in the folder ({0}) does not match - found as {1}.",
                        jahr,
                        gefundenesJahr)
                };
            }
        }

        return Ok(new
        {
            gefunden = treffer.Gefunden,
            titel = treffer.Titel,
            jahr = treffer.Jahr,
            tmdbId = treffer.TmdbId,
            anbieter = treffer.Anbieter,
            kennung = treffer.Kennung,
            hinweis = treffer.Hinweis
        });
    }

    /// <summary>All manual mappings.</summary>
    [HttpGet("Zuordnungen")]
    public ActionResult<object> Liste()
    {
        var zustand = Zustand.Laden(Zustandsdatei);
        var liste = zustand.Zuordnungen
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new
            {
                ordner = kv.Key,
                anbieter = string.IsNullOrWhiteSpace(kv.Value.Anbieter) ? "tmdb" : kv.Value.Anbieter,
                kennung = string.IsNullOrWhiteSpace(kv.Value.Kennung) ? kv.Value.TmdbId : kv.Value.Kennung,
                tmdbId = kv.Value.TmdbId,
                name = kv.Value.Name,
                jahr = kv.Value.Jahr,
                herkunft = kv.Value.Herkunft
            })
            .ToList();

        return Ok(new { anzahl = liste.Count, eintraege = liste });
    }

    /// <summary>Creates a mapping or overwrites it.</summary>
    [HttpPost("Zuordnung")]
    public async Task<ActionResult<object>> Speichern([FromBody] ZuordnungAnfrage anfrage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(anfrage.Ordner))
        {
            return BadRequest(new { hinweis = "Source folder missing" });
        }

        var cfg = Config;
        var treffer = await Kennungen
            .PruefenAsync(anfrage.Anbieter ?? "tmdb", anfrage.Kennung ?? string.Empty, anfrage.Serie, cfg.TmdbSchluessel, Engine.Sprache.Wert(cfg.TmdbSprache), ct, cfg.OmdbSchluessel, HttpContext.RequestServices)
            .ConfigureAwait(false);

        var ungeprueft = false;
        if (!treffer.Gefunden)
        {
            // Without a key nothing can be confirmed. A plain
            // TMDB number may still go in - otherwise the feature would
            // not be usable without a key at all.
            var ohneSchluessel = string.IsNullOrWhiteSpace(cfg.TmdbSchluessel);
            var reineZahl = (anfrage.Kennung ?? string.Empty).Trim().All(char.IsDigit);
            if (ohneSchluessel && reineZahl && string.Equals(anfrage.Anbieter ?? "tmdb", "tmdb", StringComparison.OrdinalIgnoreCase))
            {
                treffer = new Kennungen.Treffer(true, anfrage.Name, anfrage.Jahr, (anfrage.Kennung ?? string.Empty).Trim(), "tmdb", (anfrage.Kennung ?? string.Empty).Trim(), null);
                ungeprueft = true;
            }
            else
            {
                return BadRequest(new { hinweis = treffer.Hinweis ?? "ID not confirmed" });
            }
        }

        // In the list the preview name may be edited before saving -
        // then the input counts, not the provider title.
        var name = string.IsNullOrWhiteSpace(anfrage.Name) ? treffer.Titel : anfrage.Name!.Trim();
        var jahrWert = anfrage.Jahr ?? treffer.Jahr;

        // Id under which name and target folder are kept: TMDB as a
        // plain number, everything else as "tvdb:407966".
        var tag = !string.IsNullOrWhiteSpace(treffer.TmdbId)
            ? treffer.TmdbId!.Trim()
            : (!string.IsNullOrWhiteSpace(treffer.Kennung)
               && !string.Equals(treffer.Anbieter, "tmdb", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(treffer.Anbieter) ? "tvdb" : treffer.Anbieter!.Trim().ToLowerInvariant())
                  + ":" + treffer.Kennung!.Trim()
                : null);

        var zustand = Zustand.Laden(Zustandsdatei);
        var ordner = anfrage.Ordner.Trim();
        zustand.Zuordnungen[ordner] = new Zuordnung
        {
            TmdbId = treffer.TmdbId ?? string.Empty,
            Anbieter = treffer.Anbieter,
            Kennung = treffer.Kennung,
            Name = name,
            Jahr = jahrWert,
            Herkunft = "manuell"
        };

        // The name belongs to the title, not to the folder: so the target folder
        // carries the title someone confirmed here.
        if (!string.IsNullOrWhiteSpace(name))
        {
            var werkName = jahrWert is null
                ? name!
                : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, jahrWert);
            if (tag is not null)
            {
                zustand.Namen[Zustand.Schluessel(anfrage.Serie ? "series" : "movie", Textregeln.IstFremd(tag) ? tag : "tmdb:" + tag)] = werkName;
            }
        }

        zustand.Speichern();
        Engine.Bestand.Verwerfen();

        return Ok(new
        {
            ungeprueft,
            ordner,
            anbieter = treffer.Anbieter,
            kennung = treffer.Kennung,
            tmdbId = treffer.TmdbId,
            tag,
            name,
            jahr = jahrWert
        });
    }

    /// <summary>
    /// Sets the categories of a title (documentaries, TV shows ...).
    /// An empty list means: back to the default. It is written on the title
    /// (id) and additionally on the source folder, so the mapping also
    /// holds when the id changes later.
    /// </summary>
    [HttpPost("Kategorie")]
    public ActionResult<object> Kategorie([FromBody] KategorieAnfrage anfrage)
    {
        if (string.IsNullOrWhiteSpace(anfrage.Ordner))
        {
            return BadRequest(new { hinweis = "Source folder missing" });
        }

        var cfg = Config;
        // Every configured category is selectable, not only those of the
        // own kind: a movie may also live in a series tree.
        var erlaubt = cfg.Kategorien
            .Where(k => !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => k.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wunsch = (anfrage.Kategorien ?? new List<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Where(erlaubt.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (anfrage.Kategorien is not null && anfrage.Kategorien.Count > 0 && wunsch.Count == 0)
        {
            return BadRequest(new { hinweis = "This category is not configured" });
        }

        var zustand = Zustand.Laden(Zustandsdatei);
        var ordner = anfrage.Ordner.Trim();
        var art = anfrage.Serie ? "series" : "movie";
        var ordnerSchluessel = (anfrage.Quelle ?? string.Empty) + "|" + ordner;
        var werkSchluessel = string.IsNullOrWhiteSpace(anfrage.Schluessel)
            ? null
            : art + "|" + (Engine.Textregeln.IstFremd(anfrage.Schluessel)
                ? anfrage.Schluessel.Trim()
                : "tmdb:" + anfrage.Schluessel.Trim());

        foreach (var schluessel in new[] { werkSchluessel, ordnerSchluessel })
        {
            if (string.IsNullOrWhiteSpace(schluessel))
            {
                continue;
            }

            if (wunsch.Count == 0)
            {
                zustand.Kategorien.Remove(schluessel);
            }
            else
            {
                zustand.Kategorien[schluessel] = wunsch.ToList();
            }
        }

        zustand.Speichern();
        Engine.Bestand.Verwerfen();

        return Ok(new { ordner, kategorien = wunsch });
    }

    /// <summary>Removes a mapping.</summary>
    [HttpDelete("Zuordnung")]
    public ActionResult<object> Loeschen([FromQuery] string? ordner)
    {
        if (string.IsNullOrWhiteSpace(ordner))
        {
            return BadRequest(new { hinweis = "Source folder missing" });
        }

        var zustand = Zustand.Laden(Zustandsdatei);
        var weg = zustand.Zuordnungen.Remove(ordner.Trim());
        if (weg)
        {
            zustand.Speichern();
            Engine.Bestand.Verwerfen();
        }

        return Ok(new { entfernt = weg });
    }

    /// <summary>State of the last run including the warning.</summary>
    [HttpGet("Status")]
    public ActionResult<object> Status()
    {
        var cfg = Config;
        var status = Engine.Laufstatus.Laden(Takt.Statusdatei(_pfade));
        return Ok(new
        {
            ergebnis = status.Ergebnis,
            laeuft = string.Equals(status.Ergebnis, "laeuft", StringComparison.Ordinal),
            fortschritt = status.Fortschritt,
            phase = status.Phase,
            letzterStart = status.LetzterStart,
            letztesEnde = status.LetztesEnde,
            dauer = status.Dauer,
            gesetzt = status.Gesetzt,
            unveraendert = status.Unveraendert,
            fehler = status.Fehler,
            meldung = status.Meldung,
            ausloeser = status.Ausloeser,
            warnung = status.Warnung(cfg.IntervallMinuten),
            intervall = cfg.IntervallMinuten
        });
    }

    /// <summary>Starts a run in the background.</summary>
    [HttpPost("Lauf")]
    public ActionResult<object> Lauf()
    {
        var status = Engine.Laufstatus.Laden(Takt.Statusdatei(_pfade));
        if (string.Equals(status.Ergebnis, "laeuft", StringComparison.Ordinal)
            && status.LetzterStart is { } start
            && DateTime.UtcNow - start.ToUniversalTime() < TimeSpan.FromMinutes(90))
        {
            return Conflict(new { hinweis = "A run is already in progress since " + start.ToLocalTime().ToString("HH:mm") });
        }

        var cfg = Config;
        _ = Task.Run(async () =>
        {
            try
            {
                await _aufgabe.AusloesenAsync("hand", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cross-Source Merge: manual start failed");
            }
        }, CancellationToken.None);

        return Ok(new { gestartet = true, nurPlanen = cfg.NurPlanen });
    }

    /// <summary>All state backups.</summary>
    [HttpGet("Sicherungen")]
    public ActionResult<object> Sicherungen()
    {
        var liste = Engine.Sicherung.Liste(_pfade)
            .Select(e => new { name = e.Name, groesse = e.Groesse, zeit = e.Zeit })
            .ToList();
        return Ok(new { anzahl = liste.Count, eintraege = liste });
    }

    /// <summary>Creates a backup now.</summary>
    [HttpPost("Sicherung")]
    public ActionResult<object> Sichern()
    {
        var cfg = Config;
        var name = Engine.Sicherung.Anlegen(_pfade, cfg.SicherungenBehalten);
        return name is null
            ? BadRequest(new { hinweis = "There is no state to back up yet." })
            : Ok(new { name });
    }

    /// <summary>Restores a backup.</summary>
    [HttpPost("Sicherung/Zurueck")]
    public ActionResult<object> Zurueck([FromBody] ZurueckAnfrage anfrage)
    {
        if (string.IsNullOrWhiteSpace(anfrage.Name))
        {
            return BadRequest(new { hinweis = "Name missing" });
        }

        if (!Engine.Sicherung.Zurueck(_pfade, anfrage.Name, out var meldung))
        {
            return BadRequest(new { hinweis = meldung });
        }

        // The state is a different one: the list must not answer from the
        // previous cache.
        Engine.Bestand.Verwerfen();

        return Ok(new { meldung });
    }

    /// <summary>What the page sends when restoring.</summary>
    public sealed class ZurueckAnfrage
    {
        /// <summary>File name of the backup.</summary>
        public string? Name { get; set; }
    }

    /// <summary>What the page sends.</summary>
    public sealed class KategorieAnfrage
    {
        /// <summary>Source folder (name is enough).</summary>
        public string? Ordner { get; set; }

        /// <summary>Tag of the source, as shown in the list.</summary>
        public string? Quelle { get; set; }

        /// <summary>Id of the title (TMDB number or tvdb:...).</summary>
        public string? Schluessel { get; set; }

        /// <summary>Whether the folder belongs to the series tree.</summary>
        public bool Serie { get; set; }

        /// <summary>Names of the categories; empty = default.</summary>
        public List<string>? Kategorien { get; set; }
    }

    public sealed class ZuordnungAnfrage
    {
        /// <summary>Source folder as named on disk.</summary>
        public string? Ordner { get; set; }

        /// <summary>tmdb, imdb or tvdb.</summary>
        public string? Anbieter { get; set; }

        /// <summary>Id at the provider.</summary>
        public string? Kennung { get; set; }

        /// <summary>Is this a series?</summary>
        public bool Serie { get; set; }

        /// <summary>Title, if saving without a key.</summary>
        public string? Name { get; set; }

        /// <summary>Year, if saving without a key.</summary>
        public int? Jahr { get; set; }
    }
}
