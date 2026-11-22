using System.Diagnostics;
using System.Globalization;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Image data of a video file - the basis for a good placeholder.</summary>
public static class MediaInfo
{
    /// <summary>Duration, width and height; all null without ffprobe.</summary>
    public static (int? Dauer, int? Breite, int? Hoehe) Erheben(string datei, string? ffprobe)
    {
        if (string.IsNullOrWhiteSpace(ffprobe))
        {
            return (null, null, null);
        }

        try
        {
            var argumente = "-v error -select_streams v:0 -show_entries stream=width,height:format=duration "
                            + "-of default=nw=1:nk=1 \"" + datei + "\"";

            using var p = Process.Start(new ProcessStartInfo(ffprobe, argumente)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (p is null)
            {
                return (null, null, null);
            }

            var ausgabe = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20_000);

            var werte = ausgabe.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(z => z.Trim())
                .Where(z => z.Length > 0)
                .ToList();

            int? breite = null;
            int? hoehe = null;
            int? dauer = null;

            if (werte.Count >= 2 &&
                int.TryParse(werte[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) &&
                int.TryParse(werte[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                breite = b;
                hoehe = h;
            }

            if (werte.Count >= 3 &&
                double.TryParse(werte[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                d > 0)
            {
                dauer = (int)d;
            }

            return (dauer, breite, hoehe);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, null, null);
        }
    }

    /// <summary>Look for ffprobe next to ffmpeg.</summary>
    public static string? Ffprobe(string? ffmpeg)
    {
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return null;
        }

        var ordner = Path.GetDirectoryName(ffmpeg);
        if (string.IsNullOrEmpty(ordner))
        {
            return null;
        }

        // On Windows the program is called ffprobe.exe.
        var kandidat = Path.Combine(ordner, Betriebssystem.Programm("ffprobe"));
        return File.Exists(kandidat) ? kandidat : null;
    }
}
