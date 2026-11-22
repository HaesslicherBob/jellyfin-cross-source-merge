using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Shadow library: if a source is missing, the entry stays in the target tree.
/// What was never there gets a small placeholder so that Jellyfin
/// keeps the entry.
/// </summary>
public static class Schatten
{
    private const int ProbeSekunden = 8;

    /// <summary>
    /// Roots that are currently unreachable. A dead network mount is checked
    /// with a time limit, otherwise the whole run hangs on it.
    /// </summary>
    public static HashSet<string> ToteWurzeln(IEnumerable<Quelle> quellen, ILogger log)
    {
        var tot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pfad in quellen.Select(q => q.Pfad).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            if (!Erreichbar(pfad))
            {
                tot.Add(pfad.TrimEnd(Path.DirectorySeparatorChar));
            }
        }

        if (tot.Count > 0)
        {
            log.LogWarning("Not reachable: {Wurzeln}", string.Join(", ", tot));
        }

        return tot;
    }

    /// <summary>Reachability check with a time limit.</summary>
    public static bool Erreichbar(string pfad)
    {
        try
        {
            var aufgabe = Task.Run(() => Directory.Exists(pfad));
            return aufgabe.Wait(TimeSpan.FromSeconds(ProbeSekunden)) && aufgabe.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>Does the path lie in a dead source (or below it)?</summary>
    public static bool Geschuetzt(string pfad, IReadOnlySet<string> tot)
    {
        foreach (var w in tot)
        {
            if (pfad.Equals(w, StringComparison.Ordinal) ||
                pfad.StartsWith(w + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Entries in the target tree whose link points into a dead shadow source.
    /// Only the link itself is read - the dead mount is not touched.
    /// </summary>
    public static Dictionary<string, string> Pfade(
        string wurzel,
        bool istSerie,
        IReadOnlySet<string> tot,
        ILogger log)
    {
        var treffer = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tot.Count == 0 || !Directory.Exists(wurzel))
        {
            return treffer;
        }

        var werkordner = new List<string>();
        try
        {
            werkordner.AddRange(Directory.GetDirectories(wurzel));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return treffer;
        }

        foreach (var ordner in werkordner)
        {
            var unter = new List<string> { ordner };
            if (istSerie)
            {
                try
                {
                    // Only one level: season folders. No recursion that would
                    // follow dead links.
                    unter.AddRange(Directory.GetDirectories(ordner));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            foreach (var u in unter)
            {
                IEnumerable<string> dateien;
                try
                {
                    dateien = Directory.GetFiles(u);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var f in dateien)
                {
                    var ziel = LinkZiel(f);
                    if (ziel is null)
                    {
                        continue;
                    }

                    if (Geschuetzt(ziel, tot))
                    {
                        treffer[f] = ziel;
                    }
                }
            }
        }

        if (treffer.Count > 0)
        {
            log.LogInformation("Shadow: {Anzahl} entries stay in place", treffer.Count);
        }

        return treffer;
    }

    /// <summary>Target of a link, otherwise null.</summary>
    public static string? LinkZiel(string pfad)
    {
        try
        {
            var info = new FileInfo(pfad);
            return info.LinkTarget is null ? null : Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(pfad)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Placeholder for a title that was never there. It carries the same
    /// extension, the same resolution and the same runtime as the original,
    /// so that Jellyfin sees the same entry and does not rescan the library.
    /// Content: black image, silent audio track.
    /// </summary>
    public static bool AttrappeAnlegen(
        string ziel,
        string werkSchluessel,
        bool istSerie,
        Zustand zustand,
        string? ffmpeg,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            log.LogWarning("Placeholder for {Ziel}: no ffmpeg found", ziel);
            return false;
        }

        try
        {
            var ordner = Path.GetDirectoryName(ziel);
            if (!string.IsNullOrEmpty(ordner))
            {
                Directory.CreateDirectory(ordner);
            }

            var (dauer, breite, hoehe) = zustand.ManifestWerte(istSerie ? "series" : "movie", werkSchluessel);

            var argumente = string.Join(' ', new[]
            {
                "-y", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", $"color=c=black:s={breite}x{hoehe}:r=24",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                "-t", dauer.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "51", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "32k", "-shortest",
                Quote(ziel)
            });

            using var prozess = Process.Start(new ProcessStartInfo(ffmpeg, argumente)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            if (prozess is null)
            {
                return false;
            }

            var fehler = prozess.StandardError.ReadToEnd();
            prozess.WaitForExit();
            if (prozess.ExitCode != 0 || !File.Exists(ziel))
            {
                log.LogWarning("Placeholder for {Ziel} failed: {Fehler}", ziel, fehler.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogWarning("Placeholder for {Ziel} not created: {Fehler}", ziel, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Find ffmpeg - on any system. Order: configured path,
    /// the version Jellyfin ships (next to the server), PATH, then the
    /// usual install locations of the respective system.
    /// </summary>
    public static string? Ffmpeg(string? vorgabe)
    {
        if (!string.IsNullOrWhiteSpace(vorgabe) && File.Exists(vorgabe))
        {
            return vorgabe;
        }

        var name = Betriebssystem.Programm("ffmpeg");

        foreach (var ordner in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg") })
        {
            var kandidat = Path.Combine(ordner, name);
            if (File.Exists(kandidat))
            {
                return kandidat;
            }
        }

        foreach (var pfad in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var kandidat = Path.Combine(pfad, name);
            if (File.Exists(kandidat))
            {
                return kandidat;
            }
        }

        foreach (var kandidat in UeblicheOrte(name))
        {
            if (File.Exists(kandidat))
            {
                return kandidat;
            }
        }

        return null;
    }

    private static IEnumerable<string> UeblicheOrte(string name)
    {
        if (Betriebssystem.Windows)
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin", "Server", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin", "Server", "ffmpeg", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ffmpeg", "bin", name)
            };
        }

        if (Betriebssystem.Mac)
        {
            return new[]
            {
                "/opt/homebrew/bin/" + name,
                "/usr/local/bin/" + name,
                "/Applications/Jellyfin.app/Contents/MacOS/" + name
            };
        }

        return new[] { "/usr/bin/" + name, "/usr/local/bin/" + name, "/usr/lib/jellyfin-ffmpeg/" + name };
    }

    private static string Quote(string wert) => "\"" + wert.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Substitute for missing episodes: the same episode from a reachable source
/// is always better than a placeholder.
/// </summary>
public sealed class Ersatzindex
{
    private readonly Dictionary<(string Werk, int Staffel, int Folge), string> _index = new();

    /// <summary>Build the index from the groups already read.</summary>
    public Ersatzindex(IReadOnlyDictionary<string, Gruppe> gruppen, IReadOnlyList<Quelle> quellen)
    {
        var erlaubt = quellen
            .Where(q => q.Art != Quellenart.Nas && Schatten.Erreichbar(q.Pfad))
            .Select(q => q.Kennung)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var g in gruppen.Values)
        {
            foreach (var (kennung, beitrag) in g.Beitraege)
            {
                if (!erlaubt.Contains(kennung))
                {
                    continue;
                }

                foreach (var (fach, dateien) in beitrag.Folgen)
                {
                    if (dateien.Count == 0)
                    {
                        continue;
                    }

                    _index.TryAdd((g.Schluessel, fach.Staffel, fach.Folge), dateien[0]);
                }
            }
        }
    }

    /// <summary>Substitute file for an episode, or null.</summary>
    public string? Fuer(string werk, int staffel, int folge)
        => _index.TryGetValue((werk, staffel, folge), out var p) ? p : null;
}
