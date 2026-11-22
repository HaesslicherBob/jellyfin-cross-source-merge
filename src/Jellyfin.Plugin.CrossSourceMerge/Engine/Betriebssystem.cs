using System.Collections.Concurrent;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// What differs per operating system. The plugin should run on another
/// machine exactly as it does here - so assume nothing that is only true
/// on Linux.
/// </summary>
public static class Betriebssystem
{
    /// <summary>Gets whether the process runs on Windows.</summary>
    public static bool Windows => OperatingSystem.IsWindows();

    /// <summary>Gets whether the process runs on macOS.</summary>
    public static bool Mac => OperatingSystem.IsMacOS();

    private static readonly ConcurrentDictionary<string, bool> Urteile = new(StringComparer.Ordinal);

    /// <summary>
    /// Program name with the extension the system expects (ffmpeg.exe).
    /// </summary>
    public static string Programm(string name) => Windows ? name + ".exe" : name;

    /// <summary>
    /// Does this folder distinguish upper and lower case? Windows and
    /// macOS do not, Linux does - and an SMB mount under Linux does not
    /// either. Therefore it is measured, not guessed.
    /// </summary>
    public static bool IgnoriertGrossKleinschreibung(string ordner)
    {
        if (Windows || Mac)
        {
            return true;
        }

        return Urteile.GetOrAdd(ordner, Probe);
    }

    /// <summary>Two paths designate the same file - according to the folder's rule.</summary>
    public static bool Gleich(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ordner = Path.GetDirectoryName(a);
        return ordner is not null && IgnoriertGrossKleinschreibung(ordner);
    }

    /// <summary>
    /// Does the folder accept symbolic links? On Windows this needs developer
    /// mode or administrator rights, on an SMB share it does not work at all.
    /// Better to check once beforehand than 80 000 error lines.
    /// </summary>
    public static bool VerweiseMoeglich(string ordner, out string grund)
    {
        grund = string.Empty;
        var sonde = Path.Combine(ordner, ".csm-verweis-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var verweis = sonde + ".link";

        try
        {
            Directory.CreateDirectory(ordner);
            File.CreateSymbolicLink(verweis, sonde);
            File.Delete(verweis);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            grund = Windows
                ? "Windows allows links only with developer mode or as administrator (" + ex.Message + ")"
                : "no permission (" + ex.Message + ")";
            return false;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            grund = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(verweis))
                {
                    File.Delete(verweis);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// File name as this system accepts it. Linux allows almost anything,
    /// Windows does not: ':' '*' '?' '"' '<' '>' '|' are forbidden, a trailing
    /// dot and space as well, and CON/NUL/COM1 are device names.
    /// </summary>
    public static string Dateiname(string name) => Dateiname(name, Windows);

    /// <summary>File name for a specific system (for the probe).</summary>
    public static string Dateiname(string name, bool windows)
    {
        var n = name ?? string.Empty;
        if (!windows)
        {
            return n;
        }

        var bau = new System.Text.StringBuilder(n.Length + 4);
        foreach (var zeichen in n)
        {
            bau.Append(zeichen switch
            {
                ':' => " - ",
                '*' or '?' or '"' or '<' or '>' or '|' => "-",
                _ when zeichen < ' ' => string.Empty,
                _ => zeichen.ToString()
            });
        }

        var sauber = System.Text.RegularExpressions.Regex.Replace(bau.ToString(), @"\s+", " ").Trim();
        sauber = sauber.TrimEnd('.', ' ');
        if (sauber.Length == 0)
        {
            return "_";
        }

        var stamm = Path.GetFileNameWithoutExtension(sauber);
        if (Reserviert(stamm))
        {
            sauber = "_" + sauber;
        }

        return sauber;
    }

    private static bool Reserviert(string stamm)
    {
        var oben = stamm.ToUpperInvariant();
        if (oben is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(oben, @"^(COM|LPT)[1-9]$");
    }

    private static bool Probe(string ordner)
    {
        try
        {
            Directory.CreateDirectory(ordner);
            var datei = Path.Combine(ordner, ".csm-grossklein-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(datei, string.Empty);
            var gefunden = File.Exists(datei.ToUpperInvariant());
            File.Delete(datei);
            return gefunden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
