using System.Diagnostics;
using System.Text.Json;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>How the last pass went.</summary>
public sealed class Laufstatus
{
    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = true };

    /// <summary>Start of the last pass.</summary>
    public DateTime? LetzterStart { get; set; }

    /// <summary>End of the last pass; null while it is running.</summary>
    public DateTime? LetztesEnde { get; set; }

    /// <summary>One of laeuft, ok, fehler or abgebrochen.</summary>
    public string Ergebnis { get; set; } = "unbekannt";

    /// <summary>Duration in seconds.</summary>
    public double Dauer { get; set; }

    /// <summary>Entries set.</summary>
    public int Gesetzt { get; set; }

    /// <summary>Entries unchanged.</summary>
    public int Unveraendert { get; set; }

    /// <summary>Errors.</summary>
    public int Fehler { get; set; }

    /// <summary>Short message about the run.</summary>
    public string? Meldung { get; set; }

    /// <summary>Who triggered the run: one of takt, hand or start.</summary>
    public string? Ausloeser { get; set; }

    /// <summary>Progress of the running pass in percent (0-100).</summary>
    public int Fortschritt { get; set; }

    /// <summary>Short phase of the pass, e.g. "start", "schreiben", "fertig".</summary>
    public string Phase { get; set; } = "unbekannt";

    /// <summary>Reads the status; if the file is missing, it starts empty.</summary>
    public static Laufstatus Laden(string pfad)
    {
        try
        {
            if (File.Exists(pfad))
            {
                return JsonSerializer.Deserialize<Laufstatus>(File.ReadAllText(pfad)) ?? new Laufstatus();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Laufstatus();
    }

    /// <summary>Writes the status atomically.</summary>
    public void Speichern(string pfad)
    {
        try
        {
            var ordner = Path.GetDirectoryName(pfad);
            if (!string.IsNullOrEmpty(ordner))
            {
                Directory.CreateDirectory(ordner);
            }

            var temp = pfad + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Optionen));
            File.Move(temp, pfad, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// What was wrong with the last run. Empty means: everything is fine.
    /// </summary>
    public string? Warnung(int intervallMinuten)
    {
        if (string.Equals(Ergebnis, "laeuft", StringComparison.Ordinal)
            && LetzterStart is { } start
            && DateTime.UtcNow - start > TimeSpan.FromMinutes(90))
        {
            return "The last pass was not finished (since "
                   + start.ToLocalTime().ToString("dd.MM. HH:mm") + "). Presumably aborted - "
                   + "the library may be incomplete.";
        }

        if (string.Equals(Ergebnis, "abgebrochen", StringComparison.Ordinal))
        {
            return "The last pass was aborted" + (Meldung is null ? "." : ": " + Meldung);
        }

        if (string.Equals(Ergebnis, "fehler", StringComparison.Ordinal))
        {
            return "The last pass ended with errors" + (Meldung is null ? "." : ": " + Meldung);
        }

        if (intervallMinuten > 0 && Dauer > intervallMinuten * 60)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "The cycle is too short: the last run took {0:0} minutes, the interval is {1} minutes. "
                + "The runs overtake each other - increase the interval or switch it off.",
                Dauer / 60,
                intervallMinuten);
        }

        if (Fehler > 0)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} entries could not be written.", Fehler);
        }

        return null;
    }
}

/// <summary>Stops foreign merge runs so that only one writes.</summary>
public static class Fremdlauf
{
    private static readonly string[] Muster = { "merge_sync.py", "merge_guard.py", "merge_sync", "merge_guard" };

    /// <summary>
    /// Terminates processes that build the same tree (old Python runs) and
    /// returns what was terminated. The own process is left untouched.
    /// </summary>
    public static IReadOnlyList<string> Beenden(Action<string> melden)
    {
        var beendet = new List<string>();

        // The procedure reads /proc - that only exists on Linux. On other
        // systems a search by process name would also be a shot in the
        // dark: we stop nothing there.
        if (!OperatingSystem.IsLinux())
        {
            return beendet;
        }

        var selbst = Environment.ProcessId;

        foreach (var ordner in SafeDirs("/proc"))
        {
            var name = Path.GetFileName(ordner);
            if (!int.TryParse(name, out var pid) || pid == selbst)
            {
                continue;
            }

            string zeile;
            try
            {
                zeile = File.ReadAllText(Path.Combine(ordner, "cmdline")).Replace('\0', ' ').Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (zeile.Length == 0 || !Muster.Any(m => zeile.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                using var prozess = Process.GetProcessById(pid);
                prozess.Kill(entireProcessTree: true);
                prozess.WaitForExit(5000);
                beendet.Add(pid + ": " + zeile);
                melden?.Invoke("Foreign merge run terminated: " + pid + " (" + zeile + ")");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return beendet;
    }

    private static IEnumerable<string> SafeDirs(string wurzel)
    {
        try
        {
            return Directory.GetDirectories(wurzel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
