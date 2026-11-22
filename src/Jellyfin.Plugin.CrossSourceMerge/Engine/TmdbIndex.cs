using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Title and year for a TMDB id. Filled from what Jellyfin knows anyway
/// (the library carries the ids), so the plugin gets by without a third-party
/// service and without a key. Optionally it asks TMDB itself - only for
/// titles Jellyfin does not know yet.
/// </summary>
public sealed class TmdbIndex
{
    private readonly Dictionary<string, (string Titel, string? Jahr)> _perId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _perName = new(StringComparer.Ordinal);

    /// <summary>Number of known ids.</summary>
    public int Anzahl => _perId.Count;

    /// <summary>Builds the index from an inventory (id, title, year).</summary>
    public static TmdbIndex AusBestand(IEnumerable<(string Id, string Titel, int? Jahr)> bestand)
    {
        var index = new TmdbIndex();
        foreach (var (id, titel, jahr) in bestand)
        {
            index.Merken(id, titel, jahr);
        }

        return index;
    }

    /// <summary>Add an entry.</summary>
    public void Merken(string id, string titel, int? jahr)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(titel))
        {
            return;
        }

        var jahrText = jahr is > 1800 ? jahr.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        _perId[id] = (titel, jahrText);

        var schluessel = Schluessel(titel, jahrText);
        if (!_perName.TryGetValue(schluessel, out var liste))
        {
            liste = new List<string>();
            _perName[schluessel] = liste;
        }

        if (!liste.Contains(id, StringComparer.Ordinal))
        {
            liste.Add(id);
        }
    }

    /// <summary>Title and year for an id.</summary>
    public (string Titel, string? Jahr)? Info(string? id)
    {
        if (id is not null && _perId.TryGetValue(id, out var eintrag))
        {
            return eintrag;
        }

        return null;
    }

    /// <summary>
    /// Id for name and year. Only unambiguous matches are returned -
    /// a wrong id is worse than none.
    /// </summary>
    public string? IdFuer(string name, string? jahr)
    {
        var k = Schluessel(name, jahr);
        if (_perName.TryGetValue(k, out var liste) && liste.Count == 1)
        {
            return liste[0];
        }

        return null;
    }

    private static string Schluessel(string titel, string? jahr)
        => Textregeln.Norm(Textregeln.UmlautDe(titel)) + "#" + (jahr ?? string.Empty);

    /// <summary>
    /// Additional ids via the TMDB search. Used only when a key is stored;
    /// every match must hit title and year, otherwise it is discarded.
    /// </summary>
    public sealed class Suche
    {
        private readonly HttpClient _http;
        private readonly string _schluessel;
        private readonly string _sprache;
        private readonly Dictionary<string, (string Id, string Titel, string? Jahr)?> _zwischenspeicher = new(StringComparer.Ordinal);

        /// <summary>New search.</summary>
        public Suche(HttpClient http, string schluessel, string sprache = "de-DE")
        {
            _http = http;
            _schluessel = schluessel;
            _sprache = sprache;
        }

        /// <summary>Number of requests made.</summary>
        public int Abfragen { get; private set; }

        /// <summary>Finds id, title and year for name and year, or null.</summary>
        public async Task<(string Id, string Titel, string? Jahr)?> FindeAsync(string name, string? jahr, bool serie, CancellationToken ct)
        {
            var k = $"{name}|{jahr}|{serie}";
            if (_zwischenspeicher.TryGetValue(k, out var bekannt))
            {
                return bekannt;
            }

            var titel = Textregeln.OhneReleaseTags(name);
            titel = System.Text.RegularExpressions.Regex.Replace(titel, @"\s*\(\d{4}\)\s*$", string.Empty).Trim();
            if (titel.Length == 0)
            {
                _zwischenspeicher[k] = null;
                return null;
            }

            var art = serie ? "tv" : "movie";
            var url = $"https://api.themoviedb.org/3/search/{art}?api_key={Uri.EscapeDataString(_schluessel)}"
                      + $"&language={Uri.EscapeDataString(_sprache)}&query={Uri.EscapeDataString(titel)}"
                      + (jahr is null ? string.Empty : $"&year={jahr}");

            (string Id, string Titel, string? Jahr)? ergebnis = null;
            try
            {
                Abfragen++;
                using var antwort = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (antwort.IsSuccessStatusCode)
                {
                    var daten = await antwort.Content.ReadFromJsonAsync<TmdbAntwort>(cancellationToken: ct).ConfigureAwait(false);
                    var treffer = daten?.Ergebnisse?.FirstOrDefault();
                    var titelFinal = treffer?.TitelFinal;
                    if (treffer is not null && !string.IsNullOrWhiteSpace(titelFinal))
                    {
                        var datenJahr = treffer.JahrFinal;
                        // Adopt only if the year matches - otherwise rather
                        // stay with the folder name.
                        if (jahr is null || datenJahr is null || datenJahr == jahr)
                        {
                            ergebnis = (treffer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), titelFinal, datenJahr);
                        }
                    }
                }
            }
            catch (HttpRequestException)
            {
                ergebnis = null;
            }
            catch (TaskCanceledException)
            {
                ergebnis = null;
            }

            _zwischenspeicher[k] = ergebnis;
            return ergebnis;
        }

        private sealed class TmdbAntwort
        {
            [JsonPropertyName("results")]
            public List<TmdbTreffer>? Ergebnisse { get; set; }
        }

        private sealed class TmdbTreffer
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }

            [JsonPropertyName("title")]
            public string? Titel { get; set; }

            [JsonPropertyName("name")]
            public string? SerienTitel { get; set; }

            [JsonPropertyName("release_date")]
            public string? Erscheinungsdatum { get; set; }

            [JsonPropertyName("first_air_date")]
            public string? ErstesDatum { get; set; }

            public string? TitelFinal => Titel ?? SerienTitel;

            public string? JahrFinal
            {
                get
                {
                    var datum = Erscheinungsdatum ?? ErstesDatum;
                    return string.IsNullOrWhiteSpace(datum) || datum.Length < 4 ? null : datum[..4];
                }
            }
        }
    }
}
