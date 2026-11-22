using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Reads title and TMDB id from an .nfo next to the medium.</summary>
public static class Nfo
{
    /// <summary>Reads &lt;title&gt; from the first .nfo in the folder.</summary>
    public static string? Titel(string ordner)
    {
        foreach (var datei in NfoDateien(ordner).Take(3))
        {
            var inhalt = Lesen(datei, 65536);
            if (inhalt is null)
            {
                continue;
            }

            var m = Regex.Match(inhalt, "<title>(.*?)</title>", RegexOptions.Singleline);
            if (!m.Success)
            {
                continue;
            }

            var titel = m.Groups[1].Value
                .Replace("<![CDATA[", string.Empty, StringComparison.Ordinal)
                .Replace("]]>", string.Empty, StringComparison.Ordinal)
                .Trim();
            titel = System.Net.WebUtility.HtmlDecode(titel).Trim();
            if (titel.Length > 0)
            {
                return titel;
            }
        }

        return null;
    }

    /// <summary>TMDB id from &lt;tmdbid&gt; or a 'tmdbid-N' notation.</summary>
    public static string? TmdbId(string ordner)
    {
        foreach (var datei in NfoDateien(ordner).Take(3))
        {
            var inhalt = Lesen(datei, 65536);
            if (inhalt is null)
            {
                continue;
            }

            var m = Regex.Match(inhalt, @"<(?:tmdbid|tmdb_id|uniqueid[^>]*type=""tmdb""[^>]*)>\s*(\d+)\s*<", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Trim('0').Length > 0)
            {
                return m.Groups[1].Value;
            }

            m = Regex.Match(inhalt, @"\[(?:tmdb|themoviedb)id[=\-](\d+)\]", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Trim('0').Length > 0)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>Year from &lt;year&gt;, if the folder name carries none.</summary>
    public static string? Jahr(string ordner)
    {
        foreach (var datei in NfoDateien(ordner).Take(3))
        {
            var inhalt = Lesen(datei, 65536);
            if (inhalt is null)
            {
                continue;
            }

            var m = Regex.Match(inhalt, @"<year>\s*(\d{4})\s*</year>", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }

            try
            {
                var doc = XDocument.Parse(inhalt[..Math.Min(inhalt.Length, 60000)]);
                var j = doc.Root?.Element("year")?.Value;
                if (int.TryParse(j, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jahr) && jahr > 1900)
                {
                    return jahr.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (System.Xml.XmlException)
            {
                // an unreadable NFO is no reason to abort
            }
        }

        return null;
    }

    private static IEnumerable<string> NfoDateien(string ordner)
    {
        try
        {
            return Directory.EnumerateFiles(ordner, "*.nfo", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? Lesen(string pfad, int maxBytes)
    {
        try
        {
            using var strom = File.OpenRead(pfad);
            var puffer = new byte[Math.Min(maxBytes, (int)Math.Min(strom.Length, maxBytes))];
            var gelesen = strom.Read(puffer, 0, puffer.Length);
            return System.Text.Encoding.UTF8.GetString(puffer, 0, gelesen);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
