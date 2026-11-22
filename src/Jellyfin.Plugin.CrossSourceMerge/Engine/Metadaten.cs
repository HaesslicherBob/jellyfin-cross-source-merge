using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Place metadata next to the link: title, year and TMDB id. Jellyfin
/// recognizes a title with it even when the folder name gives nothing away.
/// Existing NFO files remain untouched.
/// </summary>
public static class Metadaten
{
    /// <summary>
    /// Writes an NFO if none exists in the title folder. Returns: written?
    /// </summary>
    public static bool SchreibeNfo(
        string werkordner,
        bool istSerie,
        string anzeigename,
        string? tmdbId,
        ILogger log)
    {
        try
        {
            if (!Directory.Exists(werkordner))
            {
                return false;
            }

            if (Directory.EnumerateFiles(werkordner, "*.nfo", SearchOption.TopDirectoryOnly).Any())
            {
                return false;
            }

            var titel = System.Text.RegularExpressions.Regex
                .Replace(anzeigename, @"\s*\[(?:tmdb|tvdb|imdb)id-[0-9a-z]+\]", string.Empty)
                .Trim();
            var jahr = Textregeln.DisplayYear(anzeigename);

            // The year stands on its own line, not in the title.
            titel = System.Text.RegularExpressions.Regex
                .Replace(titel, @"\s*\(\d{4}\)\s*$", string.Empty)
                .Trim();
            if (titel.Length == 0)
            {
                titel = anzeigename;
            }

            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
            xml.AppendLine(istSerie ? "<tvshow>" : "<movie>");
            xml.AppendLine("  <title>" + Sicher(titel) + "</title>");
            if (!string.IsNullOrWhiteSpace(jahr))
            {
                xml.AppendLine("  <year>" + Sicher(jahr!) + "</year>");
            }

            if (!string.IsNullOrWhiteSpace(tmdbId) && Textregeln.IstFremd(tmdbId))
            {
                // Foreign tag (tvdb:407966): Jellyfin's TVDB provider reads
                // <tvdbid> and maps the title by it - the folder name
                // alone would only be a name suggestion.
                var teile = tmdbId!.Split(':', 2);
                var art = teile[0].ToLowerInvariant();
                xml.AppendLine("  <" + art + "id>" + Sicher(teile[1]) + "</" + art + "id>");
                xml.AppendLine("  <uniqueid type=\"" + art + "\" default=\"true\">" + Sicher(teile[1]) + "</uniqueid>");
            }
            else if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                xml.AppendLine("  <uniqueid type=\"tmdb\" default=\"true\">" + Sicher(tmdbId!) + "</uniqueid>");
            }

            xml.AppendLine(istSerie ? "</tvshow>" : "</movie>");

            var ziel = Path.Combine(werkordner, istSerie ? "tvshow.nfo" : "movie.nfo");
            File.WriteAllText(ziel, xml.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.LogDebug("NFO for {Ordner} not written: {Fehler}", werkordner, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Corrects a year that is in truth the TMDB id ("Training Day"
    /// with tmdbid 2034 was listed as the year 2034). Only this one case is
    /// touched - a year someone set themselves stays as it is.
    /// </summary>
    /// <returns>Was the file changed?</returns>
    public static bool JahrKorrigieren(
        string werkordner,
        bool istSerie,
        string anzeigename,
        string? tmdbId,
        ILogger log)
    {
        if (string.IsNullOrWhiteSpace(tmdbId) || tmdbId.Length < 4 || Textregeln.IstFremd(tmdbId))
        {
            return false;
        }

        var richtig = Textregeln.DisplayYear(anzeigename);
        if (richtig is null)
        {
            return false;
        }

        try
        {
            var datei = Path.Combine(werkordner, istSerie ? "tvshow.nfo" : "movie.nfo");
            if (!File.Exists(datei))
            {
                datei = Directory.EnumerateFiles(werkordner, "*.nfo", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault() ?? string.Empty;
                if (datei.Length == 0)
                {
                    return false;
                }
            }

            var text = File.ReadAllText(datei);
            var m = Regex.Match(text, @"<year>\s*(\d{4})\s*</year>");
            if (!m.Success || m.Groups[1].Value == richtig)
            {
                return false;
            }

            // Confusion only if the existing year is the first four digits
            // of the id. Otherwise someone meant something else.
            if (!m.Groups[1].Value.Equals(tmdbId[..4], StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(datei, text.Remove(m.Index, m.Length)
                .Insert(m.Index, "<year>" + Sicher(richtig) + "</year>"), new UTF8Encoding(false));
            log.LogInformation("NFO year corrected: {Datei} {Alt} -> {Neu}", datei, m.Groups[1].Value, richtig);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug("NFO year not checked: {Fehler}", ex.Message);
            return false;
        }
    }

    private static string Sicher(string wert) => SecurityElement.Escape(wert) ?? string.Empty;
}
