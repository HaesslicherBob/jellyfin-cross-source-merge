namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Language for TMDB and OMDB. Empty means: the language of the server - the
/// schedule knows it at startup and stores it here. A hard-wired
/// "de-DE" would be wrong on every other machine.
/// </summary>
public static class Sprache
{
    /// <summary>Gets or sets the language of the server (ISO, e.g. de-DE).</summary>
    public static string Standardwert { get; set; } = "en-US";

    /// <summary>The effective value: setting, otherwise the server language.</summary>
    public static string Wert(string? eingestellt)
    {
        var eigen = eingestellt?.Trim();
        return string.IsNullOrEmpty(eigen) ? Standardwert : eigen;
    }

    /// <summary>Build a value from language and country (de + DE = de-DE).</summary>
    public static string Aus(string? sprache, string? land)
    {
        var s = (sprache ?? string.Empty).Trim();
        var l = (land ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return "en-US";
        }

        if (s.Contains('-', StringComparison.Ordinal))
        {
            return s;
        }

        // Do not guess without a country: "pt" would otherwise be pt-PT, Brazil needs pt-BR.
        return l.Length == 0 ? s : s + "-" + l.ToUpperInvariant();
    }
}
