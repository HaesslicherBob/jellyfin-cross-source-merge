using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Tags from the outside: TMDB, IMDB, TVDB. IMDB and TVDB are resolved
/// through TMDB so that the target tree carries a TMDB id in any case -
/// Jellyfin understands that without an extra plugin.
/// </summary>
public static class Kennungen
{
    /// <summary>Result of a check.</summary>
    public sealed record Treffer(
        bool Gefunden,
        string? Titel,
        int? Jahr,
        string? TmdbId,
        string Anbieter,
        string Kennung,
        string? Hinweis);

    /// <summary>Checks a tag at TMDB and returns title, year and TMDB id.</summary>
    /// <remarks>
    /// IMDB and TVDB are resolved through TMDB first so that the target tree
    /// carries an [tmdbid-...]. If TMDB has no counterpart (small
    /// series, regional titles), TVDB is asked directly at the loaded TVDB
    /// provider - then the tag appears as [tvdbid-...] in the name.
    /// </remarks>
    public static async Task<Treffer> PruefenAsync(
        string anbieter,
        string kennung,
        bool serie,
        string? schluessel,
        string sprache,
        CancellationToken ct,
        string? omdbSchluessel = null,
        IServiceProvider? dienste = null)
    {
        var art = (anbieter ?? "tmdb").Trim().ToLowerInvariant();
        var id = (kennung ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            return new Treffer(false, null, null, null, art, id, "Tag missing");
        }

        if (art == "omdb")
        {
            return await OmdbAsync(id, serie, schluessel, sprache, omdbSchluessel, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(schluessel))
        {
            if (art == "tvdb")
            {
                return await DirektTvdb(id, serie, art, ct, dienste).ConfigureAwait(false);
            }

            return new Treffer(false, null, null, null, art, id,
                "Without a TMDB key no tag can be checked - please enter it in the 'Erkennung' field.");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var spracheText = string.IsNullOrWhiteSpace(sprache) ? "de-DE" : sprache;
            var key = Uri.EscapeDataString(schluessel);

            if (art == "tmdb")
            {
                if (!id.All(char.IsDigit))
                {
                    return new Treffer(false, null, null, null, art, id, "TMDB ids are numbers");
                }

                var pfad = serie ? "tv" : "movie";
                var url = $"https://api.themoviedb.org/3/{pfad}/{Uri.EscapeDataString(id)}?api_key={key}&language={Uri.EscapeDataString(spracheText)}";
                using var antwort = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!antwort.IsSuccessStatusCode)
                {
                    return new Treffer(false, null, null, null, art, id, $"TMDB does not know {id} ({antwort.StatusCode})");
                }

                using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var wurzel = doc.RootElement;
                var titel = serie ? Text(wurzel, "name") : Text(wurzel, "title");
                var datum = serie ? Text(wurzel, "first_air_date") : Text(wurzel, "release_date");
                return new Treffer(true, titel, Jahr(datum), id, art, id, null);
            }

            var quelle = art switch
            {
                "imdb" => "imdb_id",
                "tvdb" => "tvdb_id",
                _ => null
            };

            if (quelle is null)
            {
                return new Treffer(false, null, null, null, art, id, $"Unknown provider '{art}'");
            }

            var findUrl = $"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(id)}?external_source={quelle}"
                          + $"&api_key={key}&language={Uri.EscapeDataString(spracheText)}";
            using var gefunden = await http.GetAsync(findUrl, ct).ConfigureAwait(false);
            if (!gefunden.IsSuccessStatusCode)
            {
                return new Treffer(false, null, null, null, art, id, $"TMDB responds {gefunden.StatusCode}");
            }

            using var fund = JsonDocument.Parse(await gefunden.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var listeName = serie ? "tv_results" : "movie_results";
            if (!fund.RootElement.TryGetProperty(listeName, out var liste) || liste.GetArrayLength() == 0)
            {
                if (art == "tvdb")
                {
                    var alternativ = await DirektTvdb(id, serie, art, ct, dienste).ConfigureAwait(false);
                    if (alternativ.Gefunden)
                    {
                        return alternativ;
                    }

                    return new Treffer(false, null, null, null, art, id,
                        $"TMDB does not know TVDB {id}, and {alternativ.Hinweis}");
                }

                return new Treffer(false, null, null, null, art, id,
                    $"TMDB finds no {(serie ? "series entry" : "movie")} for {art.ToUpperInvariant()} {id}");
            }

            var treffer = liste[0];
            var tmdb = treffer.TryGetProperty("id", out var idFeld) ? idFeld.GetInt32().ToString(CultureInfo.InvariantCulture) : null;
            var name = serie ? Text(treffer, "name") : Text(treffer, "title");
            var jahr = serie ? Text(treffer, "first_air_date") : Text(treffer, "release_date");
            return new Treffer(true, name, Jahr(jahr), tmdb, art, id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Treffer(false, null, null, null, art, id, "TMDB not reachable: " + ex.Message);
        }
    }

    /// <summary>
    /// Ask TVDB directly - without TMDB and without a separate key. The tag
    /// stays a TVDB tag (TmdbId null) so that the target name
    /// carries [tvdbid-...].
    /// </summary>
    private static async Task<Treffer> DirektTvdb(
        string id,
        bool serie,
        string art,
        CancellationToken ct,
        IServiceProvider? dienste)
    {
        if (!id.All(char.IsDigit))
        {
            return new Treffer(false, null, null, null, art, id, "TVDB ids are numbers");
        }

        var treffer = await TvdbNachschlag.HoleAsync(dienste, id, serie, ct).ConfigureAwait(false);
        if (treffer is null || string.IsNullOrWhiteSpace(treffer.Value.Titel))
        {
            return new Treffer(false, null, null, null, art, id,
                "TVDB direct: " + (TvdbNachschlag.LetzterHinweis ?? "no match"));
        }

        return new Treffer(true, treffer.Value.Titel, treffer.Value.Jahr, null, art, id,
            "Not at TMDB - tag stays TVDB");
    }

    /// <summary>Search by title, returns the first match with a TMDB id.</summary>
    public static async Task<Treffer> SuchenAsync(
        string name,
        string? jahr,
        bool serie,
        string? schluessel,
        string sprache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schluessel))
        {
            return new Treffer(false, null, null, null, "tmdb", string.Empty,
                "Without a TMDB key no title search - please enter it in the 'Erkennung' field.");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var suche = new TmdbIndex.Suche(http, schluessel, string.IsNullOrWhiteSpace(sprache) ? "de-DE" : sprache);
            var treffer = await suche.FindeAsync(name, jahr, serie, ct).ConfigureAwait(false);
            return treffer is null
                ? new Treffer(false, null, null, null, "tmdb", string.Empty, $"No match for '{name}'")
                : new Treffer(true, treffer.Value.Titel, Jahr(treffer.Value.Jahr), treffer.Value.Id, "tmdb", treffer.Value.Id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Treffer(false, null, null, null, "tmdb", string.Empty, "TMDB not reachable: " + ex.Message);
        }
    }

    /// <summary>
    /// OMDB knows movies and series and returns title, year and IMDB id.
    /// The target name needs a TMDB id - if a TMDB key is present, it is
    /// resolved from the IMDB id.
    /// </summary>
    private static async Task<Treffer> OmdbAsync(
        string kennung,
        bool serie,
        string? tmdbSchluessel,
        string sprache,
        string? omdbSchluessel,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(omdbSchluessel))
        {
            return new Treffer(false, null, null, null, "omdb", kennung,
                "OMDB is missing the key (field 'OMDB-Schluessel').");
        }

        if (!kennung.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return new Treffer(false, null, null, null, "omdb", kennung,
                "OMDB needs an IMDB id of the form tt0111161.");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var url = "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(omdbSchluessel.Trim())
                      + "&i=" + Uri.EscapeDataString(kennung);
            using var antwort = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!antwort.IsSuccessStatusCode)
            {
                return new Treffer(false, null, null, null, "omdb", kennung, $"OMDB responds {antwort.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var wurzel = doc.RootElement;
            if (Text(wurzel, "Response") is "False")
            {
                return new Treffer(false, null, null, null, "omdb", kennung,
                    Text(wurzel, "Error") ?? "OMDB does not know this tag");
            }

            var titel = Text(wurzel, "Title");
            var jahr = Jahr(Text(wurzel, "Year"));

            // Series check: OMDB lists both under the same tag; the
            // type is in the field Type.
            var typ = Text(wurzel, "Type");
            if (typ is not null && serie != string.Equals(typ, "series", StringComparison.OrdinalIgnoreCase))
            {
                return new Treffer(false, null, null, null, "omdb", kennung,
                    $"OMDB lists {kennung} as '{typ}', but {(serie ? "a series" : "a movie")} was requested.");
            }

            if (string.IsNullOrWhiteSpace(titel))
            {
                return new Treffer(false, null, null, null, "omdb", kennung, "OMDB returns no title");
            }

            // Resolve the TMDB id via the IMDB id so that the target name
            // carries the tag that Jellyfin understands.
            if (!string.IsNullOrWhiteSpace(tmdbSchluessel))
            {
                var ueberTmdb = await PruefenAsync("imdb", kennung, serie, tmdbSchluessel, sprache, ct).ConfigureAwait(false);
                if (ueberTmdb.Gefunden && !string.IsNullOrWhiteSpace(ueberTmdb.TmdbId))
                {
                    return new Treffer(true, ueberTmdb.Titel ?? titel, ueberTmdb.Jahr ?? jahr, ueberTmdb.TmdbId, "omdb", kennung, null);
                }
            }

            return new Treffer(true, titel, jahr, null, "omdb", kennung,
                "Without a TMDB key the target name stays without a tag.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new Treffer(false, null, null, null, "omdb", kennung, "OMDB not reachable: " + ex.Message);
        }
    }

    private static string? Text(JsonElement elem, string name)
        => elem.TryGetProperty(name, out var feld) && feld.ValueKind == JsonValueKind.String
            ? feld.GetString()
            : null;

    private static int? Jahr(string? datum)
        => datum is { Length: >= 4 } && int.TryParse(datum[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var jahr)
            ? jahr
            : null;
}
