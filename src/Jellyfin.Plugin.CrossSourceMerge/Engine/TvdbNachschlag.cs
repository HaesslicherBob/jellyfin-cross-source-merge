using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// Lookup directly at TheTVDB - via the provider that runs in this
/// Jellyfin instance anyway. That way the plugin needs no TVDB key of its
/// own, and exactly the configuration Jellyfin uses for its library applies.
///
/// Only for cases that do not exist at TMDB: TMDB remains the first choice,
/// because the target tree is mapped cleanly via [tmdbid-...] in every
/// Jellyfin without an extra plugin.
/// </summary>
public static class TvdbNachschlag
{
    /// <summary>Why the last lookup returned nothing (diagnostics).</summary>
    public static string? LetzterHinweis { get; private set; }

    /// <summary>
    /// Fetches title and year for a TVDB id. Null if no TVDB provider
    /// is loaded or it does not know the id.
    /// </summary>
    public static async Task<(string? Titel, int? Jahr)?> HoleAsync(
        IServiceProvider? dienste,
        string kennung,
        bool serie,
        CancellationToken ct)
    {
        LetzterHinweis = null;

        if (dienste is null)
        {
            LetzterHinweis = "no service provider passed";
            return null;
        }

        var id = (kennung ?? string.Empty).Trim();
        if (id.Length == 0 || !id.All(char.IsDigit))
        {
            LetzterHinweis = "TVDB ids are numbers";
            return null;
        }

        try
        {
            // Way 1: the server's own search - it knows all providers
            // set up in this instance (including those from plugins).
            var manager = dienste.GetService<IProviderManager>();
            if (manager is not null)
            {
                if (serie)
                {
                    var frage = new SeriesInfo
                    {
                        MetadataLanguage = "de",
                        MetadataCountryCode = "DE"
                    };
                    frage.ProviderIds["Tvdb"] = id;
                    var treffer = await manager.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo> { SearchInfo = frage }, ct).ConfigureAwait(false);
                    var erster = treffer.FirstOrDefault(t => t.Name is { Length: > 0 });
                    if (erster is not null)
                    {
                        return (erster.Name, erster.ProductionYear);
                    }
                }
                else
                {
                    var frage = new MovieInfo
                    {
                        MetadataLanguage = "de",
                        MetadataCountryCode = "DE"
                    };
                    frage.ProviderIds["Tvdb"] = id;
                    var treffer = await manager.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo> { SearchInfo = frage }, ct).ConfigureAwait(false);
                    var erster = treffer.FirstOrDefault(t => t.Name is { Length: > 0 });
                    if (erster is not null)
                    {
                        return (erster.Name, erster.ProductionYear);
                    }
                }

                LetzterHinweis = "server search without a match (TVDB plugin not active?)";
            }

            if (serie)
            {
                var alle = dienste.GetServices<IRemoteMetadataProvider<Series, SeriesInfo>>().ToList();
                if (alle.Count == 0)
                {
                    var roh = dienste.GetServices<IMetadataProvider>().Select(p => p.GetType().Name).Distinct().ToList();
                    LetzterHinweis = "no IRemoteMetadataProvider<Series, SeriesInfo> registered; "
                                     + $"IMetadataProvider total: {roh.Count}"
                                     + (roh.Count > 0 ? " [" + string.Join(", ", roh.Take(12)) + "]" : string.Empty);
                }

                foreach (var anbieter in alle)
                {
                    if (!IstTvdb(anbieter))
                    {
                        continue;
                    }

                    var frage = new SeriesInfo
                    {
                        MetadataLanguage = "de",
                        MetadataCountryCode = "DE"
                    };
                    frage.ProviderIds["Tvdb"] = id;
                    var ergebnis = await anbieter.GetMetadata(frage, ct).ConfigureAwait(false);
                    if (ergebnis?.Item?.Name is { Length: > 0 } name)
                    {
                        return (name, ergebnis.Item.ProductionYear);
                    }

                    LetzterHinweis = $"TVDB provider {anbieter.GetType().Name} returned no match";
                }

                LetzterHinweis ??= $"no TVDB provider among {alle.Count} series providers";
                return null;
            }

            var filme = dienste.GetServices<IRemoteMetadataProvider<Movie, MovieInfo>>().ToList();
            if (filme.Count == 0)
            {
                LetzterHinweis = "no IRemoteMetadataProvider<Movie, MovieInfo> registered";
            }

            foreach (var anbieter in filme)
            {
                if (!IstTvdb(anbieter))
                {
                    continue;
                }

                var frage = new MovieInfo
                {
                    MetadataLanguage = "de",
                    MetadataCountryCode = "DE"
                };
                frage.ProviderIds["Tvdb"] = id;
                var ergebnis = await anbieter.GetMetadata(frage, ct).ConfigureAwait(false);
                if (ergebnis?.Item?.Name is { Length: > 0 } name)
                {
                    return (name, ergebnis.Item.ProductionYear);
                }

                LetzterHinweis = $"TVDB provider {anbieter.GetType().Name} returned no match";
            }

            LetzterHinweis ??= $"no TVDB provider among {filme.Count} movie providers";
            return null;
        }
        catch (Exception ex)
        {
            LetzterHinweis = "exception: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static bool IstTvdb(object anbieter)
    {
        var typ = anbieter.GetType().FullName ?? string.Empty;
        return typ.Contains("Tvdb", StringComparison.OrdinalIgnoreCase);
    }
}
