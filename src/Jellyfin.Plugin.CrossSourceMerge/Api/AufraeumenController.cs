using Jellyfin.Plugin.CrossSourceMerge.Engine;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Api;

/// <summary>
/// "Cleanup" tab: list targets without a source and move them one by one or
/// all of them to &lt;target&gt;-alt/aufraeumen/&lt;date&gt;.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/CrossSourceMerge")]
public class AufraeumenController : ControllerBase
{
    private readonly IApplicationPaths _pfade;
    private readonly ILogger<AufraeumenController> _log;

    /// <summary>New controller.</summary>
    public AufraeumenController(IApplicationPaths pfade, ILogger<AufraeumenController> log)
    {
        _pfade = pfade;
        _log = log;
    }

    private string Solldatei => Path.Combine(_pfade.PluginConfigurationsPath, "crosssource-soll.json");

    /// <summary>Lists the targets without a source.</summary>
    [HttpGet("Aufraeumen")]
    public ActionResult<object> Liste()
    {
        var soll = Sollstand.Laden(Solldatei);
        var (zeilen, gesamt) = Sammeln(soll);
        return Ok(new
        {
            stand = soll.Stand,
            bekannt = soll.Kategorien.Count > 0,
            gesamt,
            zeilen
        });
    }

    /// <summary>Moves the selected entries, or all of them, to the holding area.</summary>
    [HttpPost("Aufraeumen/Entfernen")]
    public ActionResult<object> Entfernen([FromBody] EntfernenAuftrag auftrag)
    {
        var soll = Sollstand.Laden(Solldatei);
        if (soll.Kategorien.Count == 0)
        {
            return Ok(new { entfernt = 0, fehler = 0, meldung = "No run yet - there is no expected-state list." });
        }

        var (zeilen, _) = Sammeln(soll);
        var gewaehlt = auftrag?.Alle == true
            ? zeilen
            : zeilen.Where(z => (auftrag?.Pfade ?? new List<string>()).Contains(z.Pfad, StringComparer.Ordinal)).ToList();

        if (gewaehlt.Count == 0)
        {
            return Ok(new { entfernt = 0, fehler = 0, meldung = "Nothing selected." });
        }

        var jetzt = DateTime.Now;
        var entfernt = 0;
        var fehler = 0;
        foreach (var z in gewaehlt)
        {
            if (Aufraeumen.Wegraeumen(z.Kategorie, z.Pfad, jetzt, out var ablage))
            {
                entfernt++;
                _log.LogInformation("Cleaned up: {Pfad} -> {Ablage}", z.Pfad, ablage);
            }
            else
            {
                fehler++;
                _log.LogWarning("Cleanup failed: {Pfad}", z.Pfad);
            }
        }

        return Ok(new
        {
            entfernt,
            fehler,
            meldung = fehler == 0
                ? entfernt + " entries moved to -alt/aufraeumen."
                : entfernt + " moved, " + fehler + " not possible (locked?)."
        });
    }

    private static (List<Aufraeumen.AltlastZeile> Zeilen, int Gesamt) Sammeln(Sollstand soll)
    {
        var alle = new List<Aufraeumen.AltlastZeile>();
        foreach (var (kategorie, liste) in soll.Kategorien)
        {
            var ordner = new HashSet<string>(liste.Ordner, StringComparer.Ordinal);
            var dateien = new HashSet<string>(liste.Dateien, StringComparer.Ordinal);
            alle.AddRange(Aufraeumen.Sammeln(kategorie, kategorie, ordner, dateien));
        }

        // Keep large lists usable: folder names first, then files.
        alle = alle
            .OrderBy(z => z.Art == "Ordner" ? 0 : 1)
            .ThenByDescending(z => z.Bytes)
            .ThenBy(z => z.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // "Remove all" must not depend on the display limit: the list
        // comes back in full, the tab only shows it shortened.
        return (alle, alle.Count);
    }

    /// <summary>Request from the tab.</summary>
    public sealed class EntfernenAuftrag
    {
        /// <summary>Individual paths.</summary>
        public List<string>? Pfade { get; set; }

        /// <summary>Everything currently considered a target without a source.</summary>
        public bool Alle { get; set; }
    }
}
