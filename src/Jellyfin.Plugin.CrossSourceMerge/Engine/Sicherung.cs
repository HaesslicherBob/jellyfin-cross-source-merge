using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Copies of the state file. The state is the only thing a run cannot
/// restore: names once chosen, manual mappings, categories.
/// </summary>
public static class Sicherung
{
    /// <summary>Folder with the backups.</summary>
    public static string Ordner(IApplicationPaths pfade)
        => Path.Combine(pfade.PluginConfigurationsPath, "sicherungen");

    /// <summary>Path of the state file.</summary>
    public static string Zustand(IApplicationPaths pfade)
        => Path.Combine(pfade.PluginConfigurationsPath, "crosssource-zustand.json");

    /// <summary>Creates a copy and removes old ones. Returns the file name.</summary>
    public static string? Anlegen(IApplicationPaths pfade, int behalten, Action<string>? melden = null)
    {
        var quelle = Zustand(pfade);
        if (!File.Exists(quelle))
        {
            return null;
        }

        try
        {
            var ordner = Ordner(pfade);
            Directory.CreateDirectory(ordner);

            var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-zustand.json";
            File.Copy(quelle, Path.Combine(ordner, name), overwrite: false);
            melden?.Invoke("Backup created: " + name);

            if (behalten > 0)
            {
                var alte = Directory.GetFiles(ordner, "*-zustand.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Skip(behalten)
                    .ToList();
                foreach (var datei in alte)
                {
                    File.Delete(datei);
                }
            }

            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            melden?.Invoke("Backup failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>All backups, newest first.</summary>
    public static IReadOnlyList<(string Name, long Groesse, DateTime Zeit)> Liste(IApplicationPaths pfade)
    {
        try
        {
            var ordner = Ordner(pfade);
            if (!Directory.Exists(ordner))
            {
                return Array.Empty<(string, long, DateTime)>();
            }

            return Directory.GetFiles(ordner, "*-zustand.json")
                .Select(f => (Path.GetFileName(f), new FileInfo(f).Length, File.GetLastWriteTime(f)))
                .OrderByDescending(e => e.Item1, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, long, DateTime)>();
        }
    }

    /// <summary>Restores a backup; the current one is backed up beforehand.</summary>
    public static bool Zurueck(IApplicationPaths pfade, string name, out string meldung)
    {
        meldung = string.Empty;
        var ordner = Ordner(pfade);
        var quelle = Path.Combine(ordner, Path.GetFileName(name));

        if (!quelle.StartsWith(ordner, StringComparison.Ordinal) || !File.Exists(quelle))
        {
            meldung = "Backup not found";
            return false;
        }

        // Check first, then replace: a truncated backup would otherwise
        // overwrite all manual mappings and categories.
        if (!Zustandsdatei.Lesbar(quelle, out var grund))
        {
            meldung = "Backup is unreadable: " + grund;
            return false;
        }

        try
        {
            var ziel = Zustand(pfade);
            if (File.Exists(ziel))
            {
                File.Copy(ziel, ziel + ".vor-rueckkehr", overwrite: true);
            }

            File.Copy(quelle, ziel, overwrite: true);
            meldung = "Restored: " + Path.GetFileName(quelle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            meldung = "Restore failed: " + ex.Message;
            return false;
        }
    }
}
