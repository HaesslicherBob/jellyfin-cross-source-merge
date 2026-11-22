using Jellyfin.Plugin.CrossSourceMerge.Engine;
using Jellyfin.Plugin.CrossSourceMerge.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge;

/// <summary>
/// The schedule: starts the merge at the configured interval and, during
/// startup, terminates foreign merge processes. Without a schedule only triggered runs happen.
/// </summary>
public sealed class Takt : IHostedService, IDisposable
{
    private readonly MergeTask _aufgabe;
    private readonly IApplicationPaths _pfade;
    private readonly ILogger<Takt> _log;
    private readonly MediaBrowser.Controller.Configuration.IServerConfigurationManager? _serverKonfiguration;
    private readonly CancellationTokenSource _ende = new();
    private Task? _schleife;
    private DateTime _naechstePruefung = DateTime.MinValue;
    private Dictionary<string, bool>? _erreichbar;

    /// <summary>New schedule.</summary>
    public Takt(
        MergeTask aufgabe,
        IApplicationPaths pfade,
        ILogger<Takt> log,
        MediaBrowser.Controller.Configuration.IServerConfigurationManager? serverKonfiguration = null)
    {
        _aufgabe = aufgabe;
        _pfade = pfade;
        _log = log;
        _serverKonfiguration = serverKonfiguration;
    }

    /// <summary>Path of the status file.</summary>
    public static string Statusdatei(IApplicationPaths pfade)
        => Path.Combine(pfade.PluginConfigurationsPath, "crosssource-status.json");

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;

        // Remember the server language once: an empty language in the settings
        // means "take the one of the server".
        try
        {
            // Since Jellyfin 12 the language is in system.xml, no longer in
            // metadata.xml: ServerConfiguration.PreferredMetadataLanguage.
            var server = _serverKonfiguration?.Configuration;
            Engine.Sprache.Standardwert = Engine.Sprache.Aus(server?.PreferredMetadataLanguage, server?.MetadataCountryCode);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cross-Source Merge: server language not readable, stays {Sprache}", Engine.Sprache.Standardwert);
        }

        if (cfg?.FremdeProzesseBeenden == true)
        {
            var beendet = Fremdlauf.Beenden(m => _log.LogInformation("{Meldung}", m));
            if (beendet.Count == 0)
            {
                _log.LogInformation("Cross-Source Merge: no foreign merge processes found");
            }
        }

        var alt = Laufstatus.Laden(Statusdatei(_pfade));
        if (string.Equals(alt.Ergebnis, "laeuft", StringComparison.Ordinal))
        {
            // The previous process did not finish. A new process means:
            // nothing of ours is running anymore - so release the status, otherwise
            // it blocks every start for 90 minutes.
            _log.LogWarning("Cross-Source Merge: the last run was not finished ({Start}), status released",
                alt.LetzterStart);
            alt.Ergebnis = "abgebrochen";
            alt.Phase = "abgebrochen";
            alt.Speichern(Statusdatei(_pfade));
        }

        _log.LogInformation("Cross-Source Merge: schedule started, interval {Intervall} minutes",
            cfg?.IntervallMinuten ?? 0);

        _schleife = Task.Run(() => SchleifeAsync(_ende.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ende.Cancel();
        if (_schleife is not null)
        {
            try
            {
                await Task.WhenAny(_schleife, Task.Delay(5000, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ende.Cancel();
        _ende.Dispose();
    }

    private async Task WachenAsync(PluginConfiguration? cfg, CancellationToken ct)
    {
        var pruef = cfg?.PruefIntervallMinuten ?? 0;
        if (pruef <= 0 || DateTime.UtcNow < _naechstePruefung)
        {
            return;
        }

        if (_erreichbar is null)
        {
            _log.LogInformation("Cross-Source Merge: source check every {Minuten} minutes", pruef);
        }

        _naechstePruefung = DateTime.UtcNow.AddMinutes(Math.Max(1, pruef));

        var jetzt = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var q in cfg?.Quellen ?? new List<QuelleKonfig>())
        {
            if (!string.IsNullOrWhiteSpace(q.Pfad))
            {
                jetzt[q.Pfad] = Schatten.Erreichbar(q.Pfad);
            }
        }

        if (jetzt.Count == 0)
        {
            return;
        }

        var zurueck = new List<string>();
        var weg = new List<string>();
        if (_erreichbar is not null)
        {
            foreach (var (pfad, da) in jetzt)
            {
                if (_erreichbar.TryGetValue(pfad, out var vorher) && vorher != da)
                {
                    (da ? zurueck : weg).Add(pfad);
                }
            }
        }

        _erreichbar = jetzt;

        if (weg.Count > 0)
        {
            _log.LogWarning("Cross-Source Merge: not reachable - {Pfade}", string.Join(", ", weg));
        }

        if (zurueck.Count == 0)
        {
            return;
        }

        _log.LogInformation("Cross-Source Merge: reachable again - {Pfade}", string.Join(", ", zurueck));

        var status = Laufstatus.Laden(Statusdatei(_pfade));
        var laeuft = string.Equals(status.Ergebnis, "laeuft", StringComparison.Ordinal)
                     && status.LetzterStart is { } start
                     && DateTime.UtcNow - start.ToUniversalTime() < TimeSpan.FromMinutes(90);
        if (!laeuft)
        {
            await _aufgabe.AusloesenAsync("waechter", ct).ConfigureAwait(false);
        }
    }

    private async Task SchleifeAsync(CancellationToken ct)
    {
        // Wait briefly: during startup Jellyfin has enough other things to do.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var intervall = cfg?.IntervallMinuten ?? 0;

                if (intervall > 0)
                {
                    var status = Laufstatus.Laden(Statusdatei(_pfade));
                    var faellig = status.LetztesEnde is null
                                  || DateTime.UtcNow - status.LetztesEnde.Value.ToUniversalTime() >= TimeSpan.FromMinutes(intervall);

                    var laeuft = string.Equals(status.Ergebnis, "laeuft", StringComparison.Ordinal)
                                 && status.LetzterStart is { } start
                                 && DateTime.UtcNow - start.ToUniversalTime() < TimeSpan.FromMinutes(90);

                    if (faellig && !laeuft)
                    {
                        _log.LogInformation("Cross-Source Merge: schedule triggers a run (interval {Intervall} minutes)", intervall);
                        await _aufgabe.AusloesenAsync("takt", ct).ConfigureAwait(false);
                    }
                }

                // Separate, short schedule only for the question "is the source there?".
                // When a NAS comes back, the merge runs immediately - otherwise
                // the shadow copy waits for the next big interval.
                await WachenAsync(cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cross-Source Merge: schedule failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
