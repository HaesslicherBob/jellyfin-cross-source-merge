using Jellyfin.Plugin.CrossSourceMerge.Engine;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CrossSourceMerge;

/// <summary>
/// Settings of the plugin: two target folders, a list of source folders and
/// optionally a TMDB key. There is deliberately no more - everything else
/// the plugin derives from the directories.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the target folder for movies (legacy field, see categories).</summary>
    public string ZielFilme { get; set; } = string.Empty;

    /// <summary>Gets or sets the target folder for series (legacy field, see categories).</summary>
    public string ZielSerien { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the categories. Each category is its own target tree;
    /// a title can be in several of them (movies, documentaries, ...).
    /// </summary>
    public List<KategorieKonfig> Kategorien { get; set; } = new();

    /// <summary>Gets or sets the source folders, sorted by rank.</summary>
    public List<QuelleKonfig> Quellen { get; set; } = new();

    /// <summary>
    /// Gets or sets the optional TMDB key. Without it the
    /// plugin only uses what Jellyfin and the .nfo files already know.
    /// </summary>
    public string TmdbSchluessel { get; set; } = string.Empty;

    /// <summary>
    /// Key for OMDB (omdbapi.com). Only needed when the provider OMDB is
    /// selected in the list of missing mappings.
    /// </summary>
    public string OmdbSchluessel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language for the TMDB search. Empty means: the language
    /// of the Jellyfin server - that is right on a foreign machine, a
    /// fixed "de-DE" would not be.
    /// </summary>
    public string TmdbSprache { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether only planning is done. Default is on, so a freshly
    /// installed plugin does not write into a tree right away.
    /// </summary>
    public bool NurPlanen { get; set; } = true;

    /// <summary>
    /// Create missing NFO files with title, year and TMDB id.
    /// Existing files are left untouched.
    /// </summary>
    public bool NfoSchreiben { get; set; } = true;

    /// <summary>
    /// Correct a year in an existing NFO when it is really the
    /// TMDB id (tmdbid 2034 stood as year 2034).
    /// </summary>
    public bool NfoJahrKorrigieren { get; set; } = true;

    /// <summary>
    /// Move folders and files no title claims anymore to &lt;target&gt;-alt.
    /// Default: off - otherwise every name change wipes
    /// half the tree.
    /// </summary>
    public bool Aufraeumen { get; set; }

    /// <summary>
    /// Gets or sets whether the merge runs when Jellyfin starts. Default is
    /// off; the schedule in the dashboard remains the usual way.
    /// </summary>
    public bool BeimStart { get; set; }

    /// <summary>
    /// Minutes between two runs. 0 means: no schedule, only on demand.
    /// </summary>
    public int IntervallMinuten { get; set; }

    /// <summary>
    /// Separate schedule for the question "is the source still there?". On every
    /// change the merge runs immediately - so a shadow copy comes back
    /// without waiting for the big interval. 0 turns the check off.
    /// </summary>
    public int PruefIntervallMinuten { get; set; } = 5;

    /// <summary>
    /// Automatically clean up orphaned targets only after this many days. Only
    /// effective when <see cref="Aufraeumen"/> is on. 0 means: immediately.
    /// </summary>
    public int AufraeumenTage { get; set; } = 30;

    /// <summary>
    /// Terminate foreign merge processes (old Python runs) at startup and before
    /// every run, so only one program writes into the target tree. Default is
    /// off: this is a cleanup aid for a specific migration, not
    /// behaviour a foreign machine may expect. Only effective
    /// under Linux.
    /// </summary>
    public bool FremdeProzesseBeenden { get; set; }

    /// <summary>How many state backups are kept.</summary>
    public int SicherungenBehalten { get; set; } = 20;
}

