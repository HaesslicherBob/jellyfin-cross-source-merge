using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CrossSourceMerge.Engine;

/// <summary>Applies the plan in the target tree - as a symlink, never as a copy.</summary>
public static class Anwenden
{
    /// <summary>
    /// Creates the entries. If a source is missing, the existing entry stays
    /// untouched: a dead link is better for Jellyfin than a deleted
    /// entry, and a hanging network mount is not touched.
    /// If nothing was ever there, a small placeholder is created.
    /// </summary>
    public static void Schreiben(
        IEnumerable<PlanEintrag> plan,
        bool nurPlanen,
        Laufbericht bericht,
        ILogger log,
        Zustand zustand,
        IReadOnlySet<string> toteWurzeln,
        Ersatzindex? ersatz,
        string? ffmpeg)
    {
        var liste = plan as IReadOnlyList<PlanEintrag> ?? plan.ToList();
        if (!nurPlanen)
        {
            var mitQuelle = liste.Count(e => e.Quelle is not null);
            var vorhanden = liste.Count(e => e.Quelle is not null &&
                (File.Exists(e.Ziel) || Schatten.LinkZiel(e.Ziel) is not null));
            log.LogInformation("Inventory before writing: {MitQuelle} entries with source, {Vorhanden} of them present in the target",
                mitQuelle, vorhanden);
        }

        foreach (var eintrag in liste)
        {
            // Shadow entry: source gone, entry stays as it is.
            if (eintrag.Quelle is null)
            {
                bericht.Schatten++;
                continue;
            }

            var quelleVorhanden = DateiVorhanden(eintrag.Quelle);
            if (nurPlanen)
            {
                if (quelleVorhanden)
                {
                    bericht.Geplant++;
                }
                else
                {
                    bericht.StehenGelassen++;
                }

                continue;
            }

            try
            {
                if (Schatten.Geschuetzt(eintrag.Quelle, toteWurzeln) ||
                    Schatten.Geschuetzt(eintrag.Ziel, toteWurzeln))
                {
                    // Dead source: neither check nor replace.
                    if (File.Exists(eintrag.Ziel) || Schatten.LinkZiel(eintrag.Ziel) is not null)
                    {
                        bericht.Schatten++;
                        continue;
                    }
                }

                var zielInfo = new FileInfo(eintrag.Ziel);
                var istVerweis = zielInfo.LinkTarget is not null;

                if (istVerweis && SameTarget(zielInfo.LinkTarget!, eintrag.Quelle) && quelleVorhanden)
                {
                    bericht.Unveraendert++;
                    continue;
                }

                if (quelleVorhanden)
                {
                    Zaehle(bericht, istVerweis ? "verweis-anders" : "ziel-fehlt");
                    if (bericht.Gruende.Values.Sum() <= 3)
                    {
                        log.LogInformation("Sample: target={Ziel} source={Quelle} link={Verweis} exists={Da}",
                            eintrag.Ziel, eintrag.Quelle, istVerweis, zielInfo.Exists);
                    }
                    Ersetzen(eintrag.Ziel, zielInfo, istVerweis);
                    File.CreateSymbolicLink(eintrag.Ziel, eintrag.Quelle);
                    bericht.Gesetzt++;
                    continue;
                }

                // Source missing. Do not touch an existing entry.
                if (zielInfo.Exists || istVerweis)
                {
                    bericht.StehenGelassen++;
                    continue;
                }

                // Another source for the same episode is better than a placeholder.
                var alt = eintrag.IstSerie && ersatz is not null
                    ? ersatz.Fuer(eintrag.WerkKennung, eintrag.Staffel, eintrag.Folge)
                    : null;
                if (alt is not null && DateiVorhanden(alt))
                {
                    var ordner = Path.GetDirectoryName(eintrag.Ziel);
                    if (!string.IsNullOrEmpty(ordner))
                    {
                        Directory.CreateDirectory(ordner);
                    }

                    File.CreateSymbolicLink(eintrag.Ziel, alt);
                    bericht.Ersatz++;
                    continue;
                }

                if (Schatten.AttrappeAnlegen(eintrag.Ziel, eintrag.WerkKennung, eintrag.IstSerie, zustand, ffmpeg, log))
                {
                    bericht.Attrappen++;
                }
                else
                {
                    bericht.FehlerMelden(ffmpeg is null
                        ? $"Placeholder failed, ffmpeg missing ({Betriebssystem.Programm("ffmpeg")} not found): {eintrag.Ziel}"
                        : $"Placeholder failed: {eintrag.Ziel}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                bericht.FehlerMelden($"{eintrag.Ziel}: {ex.Message}");
                log.LogWarning("Entry {Ziel} could not be created: {Fehler}", eintrag.Ziel, ex.Message);
            }
        }
    }

    private static void Zaehle(Laufbericht bericht, string grund)
    {
        bericht.Gruende[grund] = bericht.Gruende.TryGetValue(grund, out var n) ? n + 1 : 1;
    }

    private static void Ersetzen(string ziel, FileInfo info, bool istVerweis)
    {
        var ordner = Path.GetDirectoryName(ziel);
        if (!string.IsNullOrEmpty(ordner))
        {
            Directory.CreateDirectory(ordner);
        }

        if (info.Exists || istVerweis)
        {
            File.Delete(ziel);
        }
    }

    private static bool DateiVorhanden(string pfad)
    {
        try
        {
            return File.Exists(pfad);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool SameTarget(string linkZiel, string quelle)
    {
        try
        {
            // On Windows, macOS and SMB shares "Film.mkv" is the same file as
            // "film.mkv". With a strict comparison every link would count as
            // wrong there and would be recreated on every run.
            return Betriebssystem.Gleich(Path.GetFullPath(linkZiel), Path.GetFullPath(quelle));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
