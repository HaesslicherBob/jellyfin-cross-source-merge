using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Leftovers in the target tree: folders and files that no title claims
/// any more. They are not deleted but moved to '&lt;target&gt;-alt' -
/// and only when it does not look like an accident.
/// </summary>
public static class Aufraeumen
{
    /// <summary>Moves orphaned folders and files. Returns: count.</summary>
    public static int Altes(
        string wurzel,
        IReadOnlySet<string> sollOrdner,
        IReadOnlySet<string> sollDateien,
        bool nurPlanen,
        ILogger log,
        int mindestAlterTage = 0)
    {
        if (!Directory.Exists(wurzel))
        {
            return 0;
        }

        var verschoben = 0;

        // Folders that no title carries any more.
        List<string> ordner;
        try
        {
            var grenzeOrdner = mindestAlterTage > 0
                ? DateTime.UtcNow.AddDays(-mindestAlterTage)
                : DateTime.MinValue;
            ordner = Directory.GetDirectories(wurzel)
                .Where(p => !sollOrdner.Contains(Path.GetFileName(p)))
                .Where(p => grenzeOrdner == DateTime.MinValue || Directory.GetLastWriteTimeUtc(p) < grenzeOrdner)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (ordner.Count > 0)
        {
            // Guard: an emptied or wrongly mapped target must not push half
            // the tree to -alt.
            var grenze = Math.Max(20, (int)(0.10 * Math.Max(sollOrdner.Count, 1)));
            if (ordner.Count > grenze)
            {
                log.LogWarning(
                    "{Anzahl} folders would be orphans (limit {Grenze}) - nothing moved. Please check categories.",
                    ordner.Count,
                    grenze);
            }
            else if (nurPlanen)
            {
                log.LogInformation("Orphans: {Anzahl} folders would move to -alt", ordner.Count);
            }
            else
            {
                var backup = wurzel + "-alt";
                foreach (var p in ordner)
                {
                    if (Verschiebe(p, Path.Combine(backup, Path.GetFileName(p))))
                    {
                        verschoben++;
                    }
                }

                if (verschoben > 0)
                {
                    log.LogInformation("Orphans: {Anzahl} folders moved to {Backup}", verschoben, backup);
                }
            }
        }

        // Files in target folders that are not in the plan.
        var dateiBackup = Path.Combine(wurzel + "-alt", "dateien");
        var dateien = new List<string>();
        try
        {
            foreach (var ordnerPfad in Directory.GetDirectories(wurzel))
            {
                foreach (var f in Directory.GetFiles(ordnerPfad, "*", SearchOption.AllDirectories))
                {
                    var endung = Path.GetExtension(f).ToLowerInvariant();
                    if (endung != ".strm" && !Scannen.VideoEndungen.Contains(endung))
                    {
                        continue;
                    }

                    if (!sollDateien.Contains(Path.GetFullPath(f))
                        && (mindestAlterTage <= 0
                            || File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-mindestAlterTage)))
                    {
                        dateien.Add(f);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return verschoben;
        }

        if (dateien.Count == 0 || nurPlanen)
        {
            if (dateien.Count > 0)
            {
                log.LogInformation("Orphans: {Anzahl} files would move to -alt/dateien", dateien.Count);
            }

            return verschoben;
        }

        foreach (var f in dateien)
        {
            var rel = Path.GetRelativePath(wurzel, f);
            if (Verschiebe(f, Path.Combine(dateiBackup, rel)))
            {
                verschoben++;
            }
        }

        if (dateien.Count > 0)
        {
            log.LogInformation("Orphans: {Anzahl} files moved to {Backup}", dateien.Count, dateiBackup);
        }

        return verschoben;
    }

    private static bool Verschiebe(string von, string nach)
    {
        try
        {
            var ziel = nach;
            var n = 1;
            while (File.Exists(ziel) || Directory.Exists(ziel))
            {
                ziel = nach + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                n++;
            }

            var ordner = Path.GetDirectoryName(ziel);
            if (!string.IsNullOrEmpty(ordner))
            {
                Directory.CreateDirectory(ordner);
            }

            if (Directory.Exists(von))
            {
                Directory.Move(von, ziel);
            }
            else
            {
                File.Move(von, ziel);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One row for the "Aufraeumen" tab: target without source.</summary>
    public sealed class AltlastZeile
    {
        /// <summary>"Ordner" or "Datei".</summary>
        public string Art { get; set; } = "Ordner";

        /// <summary>Full path in the target tree.</summary>
        public string Pfad { get; set; } = string.Empty;

        /// <summary>Name without category.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Target path of the category the entry belongs to.</summary>
        public string Kategorie { get; set; } = string.Empty;

        /// <summary>Number of files (for folders).</summary>
        public int Dateien { get; set; }

        /// <summary>Size in bytes.</summary>
        public long Bytes { get; set; }

        /// <summary>Most recent change in the entry.</summary>
        public DateTime Geaendert { get; set; }
    }

    private static readonly string[] StrukturNamen =
    {
        "movie", "tvshow", "season", "poster", "fanart", "banner", "thumb", "logo",
        "clearlogo", "clearart", "disc", "discart", "folder", "backdrop", "theme"
    };

    private static readonly string[] BegleitEndungen =
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".srt", ".ass", ".vtt", ".sub", ".tbn"
    };

    /// <summary>
    /// Collects targets without source: folders that no title carries, and
    /// files that are not in the plan. Nothing is moved - that is done by the
    /// call from the tab.
    /// </summary>
    public static List<AltlastZeile> Sammeln(
        string wurzel,
        string kategorie,
        IReadOnlySet<string> sollOrdner,
        IReadOnlySet<string> sollDateien,
        int mindestAlterTage = 0,
        ILogger? log = null)
    {
        var zeilen = new List<AltlastZeile>();
        if (!Directory.Exists(wurzel))
        {
            return zeilen;
        }

        var grenze = mindestAlterTage > 0
            ? DateTime.UtcNow.AddDays(-mindestAlterTage)
            : DateTime.MinValue;

        string[] ordner;
        try
        {
            ordner = Directory.GetDirectories(wurzel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return zeilen;
        }

        foreach (var o in ordner)
        {
            if (sollOrdner.Contains(Path.GetFileName(o)))
            {
                continue;
            }

            var zeile = new AltlastZeile
            {
                Art = "Ordner",
                Pfad = Path.GetFullPath(o),
                Name = Path.GetFileName(o),
                Kategorie = kategorie
            };
            try
            {
                foreach (var f in Directory.GetFiles(o, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var st = new FileInfo(f);
                        zeile.Dateien++;
                        zeile.Bytes += st.Length;
                        if (st.LastWriteTimeUtc > zeile.Geaendert)
                        {
                            zeile.Geaendert = st.LastWriteTimeUtc;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (zeile.Geaendert >= grenze && grenze != DateTime.MinValue)
            {
                continue;
            }

            zeilen.Add(zeile);
        }

        // Files: everything that is not in the plan. Companion files (nfo,
        // images, subtitles) count as orphaned when their media file is gone.
        foreach (var o in ordner)
        {
            if (!sollOrdner.Contains(Path.GetFileName(o)))
            {
                continue; // already recorded as a folder
            }

            string[] dateien;
            try
            {
                dateien = Directory.GetFiles(o, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in dateien)
            {
                var voll = Path.GetFullPath(f);
                if (sollDateien.Contains(voll))
                {
                    continue;
                }

                var endung = Path.GetExtension(f).ToLowerInvariant();
                var istMedium = Scannen.VideoEndungen.Contains(endung);
                if (!istMedium)
                {
                    // Companion material: only touch it when it belongs to an
                    // old name that is no longer planned. movie.nfo, tvshow.nfo,
                    // poster.jpg and the like stay where they are - they
                    // describe the folder, not a single file.
                    var stamm = Path.GetFileNameWithoutExtension(f);
                    var struktur = StrukturNamen.Contains(stamm.ToLowerInvariant());
                    if (struktur || !BegleitEndungen.Contains(endung) || !stamm.Contains(" - ", StringComparison.Ordinal)
                        || HatMedium(f, sollDateien))
                    {
                        continue;
                    }
                }

                FileInfo st;
                try
                {
                    st = new FileInfo(f);
                    if (grenze != DateTime.MinValue && st.LastWriteTimeUtc >= grenze)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                zeilen.Add(new AltlastZeile
                {
                    Art = "Datei",
                    Pfad = voll,
                    Name = Path.GetFileName(f),
                    Kategorie = kategorie,
                    Dateien = 1,
                    Bytes = st.Length,
                    Geaendert = st.LastWriteTimeUtc
                });
            }
        }

        if (log is not null && zeilen.Count > 0)
        {
            log.LogInformation(
                "Cleanup / {Kategorie}: {Anzahl} targets without source",
                Path.GetFileName(kategorie),
                zeilen.Count);
        }

        return zeilen;
    }

    private static bool HatMedium(string begleiter, IReadOnlySet<string> sollDateien)
    {
        var stamm = Path.Combine(
            Path.GetDirectoryName(begleiter) ?? string.Empty,
            Path.GetFileNameWithoutExtension(begleiter));

        foreach (var endung in Scannen.VideoEndungen)
        {
            if (sollDateien.Contains(Path.GetFullPath(stamm + endung)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Moves an entry to &lt;target&gt;-alt/aufraeumen/&lt;date&gt; and keeps
    /// the relative path while doing so.
    /// </summary>
    public static bool Wegraeumen(string kategorie, string pfad, DateTime wann, out string ziel)
    {
        ziel = string.Empty;
        var wurzel = Path.GetFullPath(kategorie).TrimEnd(Path.DirectorySeparatorChar);
        var voll = Path.GetFullPath(pfad);
        if (!voll.StartsWith(wurzel + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        var rel = Path.GetRelativePath(wurzel, voll);
        var ablege = Path.Combine(
            wurzel + "-alt",
            "aufraeumen",
            wann.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            rel);

        var ordner = Path.GetDirectoryName(ablege);
        if (!string.IsNullOrEmpty(ordner))
        {
            Directory.CreateDirectory(ordner);
        }

        try
        {
            if (Directory.Exists(voll))
            {
                if (Directory.Exists(ablege))
                {
                    ablege += "-" + wann.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                }

                Directory.Move(voll, ablege);
            }
            else if (File.Exists(voll))
            {
                File.Move(voll, ablege, overwrite: true);
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        ziel = ablege;
        return true;
    }
}
