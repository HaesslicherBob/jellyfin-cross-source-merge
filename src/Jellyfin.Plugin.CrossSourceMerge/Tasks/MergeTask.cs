using Jellyfin.Plugin.CrossSourceMerge.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Tasks;

/// <summary>
/// Builds the merged trees from the configured source folders.
/// </summary>
/// <remarks>
/// The plugin needs no external program: it reads the sources, groups
/// them into titles, names them by the TMDB rule and creates one link
/// per source in the target tree. It scans nothing and creates no schedule.
/// </remarks>
public class MergeTask : IScheduledTask
{
    private readonly ILibraryManager _bibliothek;
    private readonly IApplicationPaths _pfade;
    private readonly ILogger<MergeTask> _logger;

    // A run is exclusive: the check via the status file is only a
    // snapshot, two clicks in quick succession would both pass and
    // would rebuild the same target tree at the same time.
    private static readonly SemaphoreSlim NurEinLauf = new(1, 1);

    /// <summary>Initializes a new instance.</summary>
    /// <param name="bibliothek">Jellyfin library (provides titles and TMDB ids).</param>
    /// <param name="pfade">Paths of the server (for the state file).</param>
    /// <param name="logger">Logger.</param>
    public MergeTask(ILibraryManager bibliothek, IApplicationPaths pfade, ILogger<MergeTask> logger)
    {
        _bibliothek = bibliothek;
        _pfade = pfade;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Cross-Source Merge";

    /// <inheritdoc />
    public string Key => "CrossSourceMerge";

    /// <inheritdoc />
    public string Description =>
        "Rebuilds the merged trees from the configured source folders. "
        + "No library scan, no separate schedule.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No schedule by itself: the run starts on demand or by
        // own planning. Whoever wants can let it run at startup.
        _logger.LogInformation(
            "Cross-Source Merge: startup trigger checked, runAtStartup={RunAtStartup}, configured={Configured}",
            Plugin.Instance?.Configuration.BeimStart,
            Plugin.Instance is not null);

        if (Plugin.Instance?.Configuration.BeimStart == true)
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.StartupTrigger
                }
            };
        }

        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => AusloesenAsync("hand", cancellationToken, progress);

    /// <summary>
    /// A run with status, backup and protection against double starts.
    /// </summary>
    /// <param name="ausloeser">takt, hand or start.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="progress">Progress (optional).</param>
    public async Task AusloesenAsync(string ausloeser, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        if (!await NurEinLauf.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Cross-Source Merge: start rejected, a run is already in progress");
            throw new InvalidOperationException("A run is already in progress.");
        }

        try
        {
            await LaufInternAsync(ausloeser, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            NurEinLauf.Release();
        }
    }

    private async Task LaufInternAsync(string ausloeser, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        var cfg = Plugin.Instance?.Configuration
                  ?? throw new InvalidOperationException("Plugin configuration missing");

        var statusdatei = Takt.Statusdatei(_pfade);

        var doppelt = Umzug.DoppelteKennungen(cfg);
        if (doppelt.Count > 0)
        {
            var text = "Id assigned more than once: " + string.Join(", ", doppelt);
            _logger.LogError(
                "Cross-Source Merge: run aborted - {Text}. Two sources with the same id "
                + "would plan the same entry twice and never map the file of the second source.",
                text);
            new Laufstatus
            {
                LetzterStart = DateTime.UtcNow,
                LetztesEnde = DateTime.UtcNow,
                Ergebnis = "fehler",
                Ausloeser = ausloeser,
                Phase = "abgebrochen",
                Meldung = text
            }.Speichern(statusdatei);
            return;
        }

        var vorher = Laufstatus.Laden(statusdatei);
        if (string.Equals(vorher.Ergebnis, "laeuft", StringComparison.Ordinal)
            && vorher.LetzterStart is { } start
            && DateTime.UtcNow - start.ToUniversalTime() < TimeSpan.FromMinutes(90))
        {
            _logger.LogWarning("Cross-Source Merge: a run is already in progress since {Start}", start);
            throw new InvalidOperationException("A run is already in progress.");
        }

        var status = new Laufstatus
        {
            LetzterStart = DateTime.UtcNow,
            Ergebnis = "laeuft",
            Ausloeser = ausloeser,
            Phase = "schreiben",
            Fortschritt = 0
        };
        status.Speichern(statusdatei);

        if (cfg.FremdeProzesseBeenden && !cfg.NurPlanen)
        {
            Fremdlauf.Beenden(m => _logger.LogInformation("{Meldung}", m));
        }

        if (!cfg.NurPlanen)
        {
            // Before writing: the state is what a run cannot
            // restore.
            Sicherung.Anlegen(_pfade, cfg.SicherungenBehalten, m => _logger.LogInformation("{Meldung}", m));
        }
        else
        {
            _logger.LogInformation("Cross-Source Merge: planning only (can be turned off in the settings) - nothing is written");
        }

        status.Phase = cfg.NurPlanen ? "planen" : "schreiben";
        status.Speichern(statusdatei);

        progress?.Report(5);
        var uhr = System.Diagnostics.Stopwatch.StartNew();
        var gespeichert = false;

        try
        {
            var lauf = new MergeLauf(_bibliothek, _pfade, _logger);
            var bericht = await lauf.AusfuehrenAsync(cfg, cancellationToken).ConfigureAwait(false);
            uhr.Stop();
            progress?.Report(100);

            status.LetztesEnde = DateTime.UtcNow;
            status.Dauer = uhr.Elapsed.TotalSeconds;
            status.Fortschritt = 100;
            status.Phase = "fertig";
            status.Gesetzt = bericht.Gesetzt;
            status.Unveraendert = bericht.Unveraendert;
            status.Fehler = bericht.FehlerZahl;
            status.Ergebnis = bericht.FehlerZahl > 0 ? "fehler" : "ok";
            status.Meldung = bericht.FehlerZahl > 0
                ? $"{bericht.FehlerZahl} entries could not be set."
                : $"{bericht.Gesetzt} set, {bericht.Unveraendert} unchanged";
            status.Speichern(statusdatei);
            gespeichert = true;

            if (bericht.FehlerZahl > 0)
            {
                var beispiel = string.Join(Environment.NewLine, bericht.Fehler.Take(10));
                var rest = bericht.NichtAufgelistet > 0
                    ? $"{Environment.NewLine}({bericht.NichtAufgelistet} more not listed)"
                    : string.Empty;
                throw new InvalidOperationException(
                    $"{bericht.FehlerZahl} entries could not be set. Examples:{Environment.NewLine}{beispiel}{rest}");
            }
        }
        catch (OperationCanceledException)
        {
            status.LetztesEnde = DateTime.UtcNow;
            status.Dauer = uhr.Elapsed.TotalSeconds;
            status.Ergebnis = "abgebrochen";
            status.Phase = "abgebrochen";
            status.Meldung = "Run was cancelled.";
            status.Speichern(statusdatei);
            throw;
        }
        catch (Exception ex)
        {
            if (!gespeichert)
            {
                status.LetztesEnde = DateTime.UtcNow;
                status.Dauer = uhr.Elapsed.TotalSeconds;
                status.Ergebnis = "fehler";
                status.Phase = "fehler";
                status.Meldung = ex.Message;
                status.Speichern(statusdatei);
            }

            throw;
        }
    }
}
