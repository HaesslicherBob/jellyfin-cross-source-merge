namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>
/// What counts as mapped. An id is the whole answer: titles that exist only in
/// a source carry their id in the folder name and never reach the library
/// index, so waiting for a library title would keep them "without mapping"
/// for good. A missing title is reported separately and never blocks.
/// </summary>
public static class Zuordnungsstatus
{
    /// <summary>One of: zugeordnet, ohne-kennung.</summary>
    public static string StatusFuer(string? istId)
        => string.IsNullOrWhiteSpace(istId) ? "ohne-kennung" : "zugeordnet";

    /// <summary>Id is there, but the library knows no title for it yet.</summary>
    public static bool OhneInfoFuer(string? istId, bool fremd, bool infoBekannt)
        => !string.IsNullOrWhiteSpace(istId) && !fremd && !infoBekannt;
}
