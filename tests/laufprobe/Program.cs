using Jellyfin.Plugin.CrossSourceMerge.Engine;
using Microsoft.Extensions.Logging.Abstractions;

// Checks the naming rules and a small run against real folders.
// Usage: dotnet run -- [source-folder ...] [--ziel=/path]
// Without arguments only the rule tests run.

var fehler = 0;
void Pruefe(string was, string erwartet, string ist)
{
    if (string.Equals(erwartet, ist, StringComparison.Ordinal))
    {
        Console.WriteLine($"  ok   {was}");
        return;
    }

    fehler++;
    Console.WriteLine($"  FAIL {was}\n       expected: {erwartet}\n       actual:   {ist}");
}

// --fremdsystem: what is different on another operating system.
if (args.Contains("--fremdsystem"))
{
    var basis = Path.Combine(Path.GetTempPath(), "csm-fremd-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(basis);

    // 1. Links: yes on a local file system, otherwise with a clear message.
    var verweise = Betriebssystem.VerweiseMoeglich(basis, out var grundVerweis);
    Pruefe("local folder accepts links", "True", verweise.ToString());
    Pruefe("no reason on success", "", grundVerweis);
    Pruefe("probe file is not left behind", "0", Directory.GetFiles(basis).Length.ToString());

    // 2. Case sensitivity is measured, not guessed.
    Pruefe("Linux folder is case-sensitive", "False", Betriebssystem.IgnoriertGrossKleinschreibung(basis).ToString());
    Pruefe("same path is equal", "True",
        Betriebssystem.Gleich(Path.Combine(basis, "Film.mkv"), Path.Combine(basis, "Film.mkv")).ToString());
    Pruefe("different case is a different path on Linux", "False",
        Betriebssystem.Gleich(Path.Combine(basis, "Film.mkv"), Path.Combine(basis, "film.mkv")).ToString());

    // 3. Program name with the extension of the system.
    var erwartet = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    Pruefe("program name matches the system", erwartet, Betriebssystem.Programm("ffmpeg"));

    // 4. Find ffmpeg via PATH - with the extension of the system.
    var bin = Path.Combine(basis, "bin");
    Directory.CreateDirectory(bin);
    var ffmpeg = Path.Combine(bin, Betriebssystem.Programm("ffmpeg"));
    File.WriteAllText(ffmpeg, "#!/bin/sh\nexit 0\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    var ffprobeDatei = Path.Combine(bin, Betriebssystem.Programm("ffprobe"));
    File.WriteAllText(ffprobeDatei, "#!/bin/sh\nexit 0\n");

    var alterPfad = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", bin);
    var gefunden = Schatten.Ffmpeg(null);
    var probe = MediaInfo.Ffprobe(gefunden);
    Environment.SetEnvironmentVariable("PATH", alterPfad);

    Pruefe("ffmpeg is found in PATH", ffmpeg, gefunden);
    Pruefe("ffprobe looked up next to ffmpeg (same extension)", Path.Combine(bin, Betriebssystem.Programm("ffprobe")), probe ?? "(not found)");

    // 6. File names: Windows forbids more than Linux.
    Pruefe("Windows: colon becomes a dash", "Dune - Part Two (2024)",
        Betriebssystem.Dateiname("Dune: Part Two (2024)", true));
    Pruefe("Windows: asterisk and question mark replaced", "A-B-C", Betriebssystem.Dateiname("A*B?C", true));
    Pruefe("Windows: quotes and angle brackets removed", "a-b-c-d-e", Betriebssystem.Dateiname("a\"b<c>d|e", true));
    Pruefe("Windows: trailing dot removed", "Ende", Betriebssystem.Dateiname("Ende...", true));
    Pruefe("Windows: trailing space removed", "Ende", Betriebssystem.Dateiname("Ende   ", true));
    Pruefe("Windows: device name gets an underscore", "_CON", Betriebssystem.Dateiname("CON", true));
    Pruefe("Windows: COM1 is a device name", "_COM1", Betriebssystem.Dateiname("COM1", true));
    Pruefe("Windows: COM10 is not one", "COM10", Betriebssystem.Dateiname("COM10", true));
    Pruefe("Windows: control characters removed", "abc", Betriebssystem.Dateiname("a\u0001b\u0007c", true));
    Pruefe("Linux: colon stays", "Dune: Part Two (2024)", Betriebssystem.Dateiname("Dune: Part Two (2024)", false));
    Pruefe("Windows: empty name becomes an underscore", "_", Betriebssystem.Dateiname("", true));

    // 5. Language: empty means server language.
    Sprache.Standardwert = "de-DE";
    Pruefe("empty setting takes the server language", "de-DE", Sprache.Wert(""));
    Pruefe("own setting wins", "en-US", Sprache.Wert("en-US"));
    Pruefe("spaces do not count", "de-DE", Sprache.Wert("   "));
    Pruefe("from language and country", "de-DE", Sprache.Aus("de", "DE"));
    Pruefe("country is upper-cased", "pt-BR", Sprache.Aus("pt", "br"));
    Pruefe("language alone stays", "de", Sprache.Aus("de", ""));
    Pruefe("English without input", "en-US", Sprache.Aus(null, null));
    Pruefe("complete value stays", "de-AT", Sprache.Aus("de-AT", "DE"));

    Console.WriteLine(fehler == 0 ? "OTHER SYSTEM OK" : "OTHER SYSTEM FAIL");
    return fehler == 0 ? 0 : 1;
}

// --sicherung: a broken backup must not replace the state.
if (args.Contains("--sicherung"))
{
    var basis = Path.Combine(Path.GetTempPath(), "csm-sicher-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(basis);

    var gut = Path.Combine(basis, "20260101-000000-zustand.json");
    File.WriteAllText(gut, "{\"Zuordnungen\":{\"Test (2000)\":{\"Name\":\"Test\"}}}");

    var kaputt = Path.Combine(basis, "20260101-000001-zustand.json");
    File.WriteAllText(kaputt, "{\"Zuordnungen\":{");

    var leere = Path.Combine(basis, "20260101-000002-zustand.json");
    File.WriteAllText(leere, "");

    var liste = Path.Combine(basis, "20260101-000003-zustand.json");
    File.WriteAllText(liste, "[1,2,3]");

    var fehlt = Path.Combine(basis, "gibtsnicht.json");

    Pruefe("good backup is readable", "True", Zustandsdatei.Lesbar(gut, out var m1).ToString());
    Pruefe("truncated backup rejected", "False", Zustandsdatei.Lesbar(kaputt, out var m2).ToString());
    Pruefe("reason is given", "True", m2.Contains("JSON").ToString());
    Pruefe("empty file rejected", "False", Zustandsdatei.Lesbar(leere, out _).ToString());
    Pruefe("no object rejected", "False", Zustandsdatei.Lesbar(liste, out var m3).ToString());
    Pruefe("reason for a list", "True", m3.Contains("Objekt").ToString());
    Pruefe("missing file rejected", "False", Zustandsdatei.Lesbar(fehlt, out _).ToString());

    Console.WriteLine(fehler == 0 ? "BACKUP OK" : "BACKUP FAIL");
    return fehler == 0 ? 0 : 1;
}

// --kategorien: a title can be in several categories, patterns apply.
if (args.Contains("--kategorien"))
{
    var log = NullLogger.Instance;
    var basis = Path.Combine(Path.GetTempPath(), "csm-kat-" + Guid.NewGuid().ToString("N")[..6]);
    var q = Path.Combine(basis, "quelle");
    foreach (var n in new[] { "Planet Erde (2006)", "Tiefsee Doku (2019)", "Irgendein Film (2020)" })
    {
        Directory.CreateDirectory(Path.Combine(q, n));
        File.WriteAllText(Path.Combine(q, n, n + ".mkv"), "x");
    }

    var katQuellen = new List<Quelle> { new() { Kennung = "1 Lokal", Art = Quellenart.Lokal, Rang = 1, Pfad = q } };
    var gruppen = Scannen.Filme(katQuellen, TmdbIndex.AusBestand(Array.Empty<(string, string, int?)>()),
        new Dictionary<string, (string, string?)>(), log);

    var liste = new List<KategorieKonfig>
    {
        new() { Name = "Filme", IstSerie = false, Zielpfad = Path.Combine(basis, "filme"), Standard = true },
        new() { Name = "Dokumentationen", IstSerie = false, Zielpfad = Path.Combine(basis, "doku"), Muster = "Doku;Dokumentation" }
    };

    var zustand = Zustand.Laden(Path.Combine(basis, "zustand.json"));
    var g = gruppen.Values.First();
    zustand.Kategorien[Kategorien.Medium(false, g.Schluessel)] = new List<string> { "Filme", "Dokumentationen" };
    var v = Kategorien.Aufloesen(liste, false, gruppen, zustand);

    Console.WriteLine($"Titles: {gruppen.Count}");
    foreach (var (k, namen) in v.OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {k} -> {string.Join(", ", namen.OrderBy(n => n, StringComparer.Ordinal))}");
    }

    var doku = v.Count(kv => kv.Value.Contains("Dokumentationen"));
    var zahlFilme = v.Count(kv => kv.Value.Contains("Filme"));
    var zahlBeide = v.Count(kv => kv.Value.Count == 2);
    Console.WriteLine($"Movies {zahlFilme} / docs {doku} / in both {zahlBeide}");

    var katPlan = Planen.Filme(gruppen, Path.Combine(basis, "doku"), katQuellen,
        v.Where(kv => kv.Value.Contains("Dokumentationen")).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal));
    Console.WriteLine($"Doc plan: {katPlan.Count} entries, e.g. {Path.GetRelativePath(basis, katPlan[0].Ziel)}");

    // Only one category set (the way via the list): the title must disappear
    // from "Filme", otherwise it sits in two libraries.
    var nurDoku = gruppen.Values.First(x => x.Anzeigename.Contains("Tiefsee"));
    zustand.Kategorien[Kategorien.Medium(false, nurDoku.Schluessel)] = new List<string> { "Dokumentationen" };
    var v2 = Kategorien.Aufloesen(liste, false, gruppen, zustand);
    var umzug = !v2[nurDoku.Schluessel].Contains("Filme") && v2[nurDoku.Schluessel].Contains("Dokumentationen");
    Console.WriteLine(umzug ? "  Relocation: docs only, no longer movies" : "  Relocation: FAIL " + string.Join(",", v2[nurDoku.Schluessel]));

    var ok = zahlFilme == 2 && doku == 2 && zahlBeide == 1 && katPlan.Count == 2 && umzug;
    Console.WriteLine(ok ? "CATEGORIES OK" : "CATEGORIES FAIL");
    Directory.Delete(basis, true);
    return ok ? 0 : 1;
}

// --schatten: dead source, existing links stay, only new ones become a placeholder.
if (args.Contains("--schatten"))
{
    var log = NullLogger.Instance;
    var basis = Path.Combine(Path.GetTempPath(), "csm-schatten-" + Guid.NewGuid().ToString("N")[..6]);
    var q = Path.Combine(basis, "quelle");
    var z = Path.Combine(basis, "ziel");
    Directory.CreateDirectory(q);
    Directory.CreateDirectory(Path.Combine(z, "Werk A"));
    File.WriteAllText(Path.Combine(q, "a.mkv"), "a");
    File.WriteAllText(Path.Combine(q, "b.mkv"), "b");
    File.CreateSymbolicLink(Path.Combine(z, "Werk A", "A - 1 Lokal.mkv"), Path.Combine(q, "a.mkv"));
    File.CreateSymbolicLink(Path.Combine(z, "Werk A", "B - 1 Lokal.mkv"), Path.Combine(q, "b.mkv"));

    var zustand = Zustand.Laden(Path.Combine(basis, "zustand.json"));
    zustand.Merken("movie", "tmdb:4711", 999, 1280, 720, 120, "/egal");

    // Source disappears.
    Directory.Delete(q, true);

    var toteListe = new List<Quelle> { new() { Kennung = "1 Lokal", Art = Quellenart.Nas, Rang = 1, Pfad = q } };
    var tot = Schatten.ToteWurzeln(toteListe, log);
    Console.WriteLine($"Dead roots: {tot.Count}");

    var gefunden = Schatten.Pfade(z, false, tot, log);
    Console.WriteLine($"Shadow entries: {gefunden.Count}");

    var plan = gefunden.Keys.Select(p => new PlanEintrag { Ziel = p, Quelle = null }).ToList();
    plan.Add(new PlanEintrag { Ziel = Path.Combine(z, "Werk Neu", "Neu - 1 Lokal.mkv"), Quelle = Path.Combine(q, "c.mkv"), WerkKennung = "tmdb:4711" });

    var bericht = new Laufbericht();
    var ffmpeg = Schatten.Ffmpeg(null);
    Anwenden.Schreiben(plan, false, bericht, log, zustand, tot, null, ffmpeg);
    Console.WriteLine($"ffmpeg: {ffmpeg ?? "(not found)"}");

    var a = Path.Combine(z, "Werk A", "A - 1 Lokal.mkv");
    var neu = Path.Combine(z, "Werk Neu", "Neu - 1 Lokal.mkv");
    Console.WriteLine($"Shadow {bericht.Schatten} / placeholders {bericht.Attrappen} / errors {bericht.FehlerZahl}");
    Console.WriteLine($"Link unchanged: {new FileInfo(a).LinkTarget is not null && new FileInfo(a).LinkTarget!.Contains("quelle")}");
    Console.WriteLine($"Placeholder present: {File.Exists(neu)}, size {(File.Exists(neu) ? new FileInfo(neu).Length : 0)}");
    Console.WriteLine($"Placeholder is a regular file: {(File.Exists(neu) && new FileInfo(neu).LinkTarget is null)}");
    var masse = MediaInfo.Erheben(neu, MediaInfo.Ffprobe(Schatten.Ffmpeg(null)));
    Console.WriteLine($"Placeholder dimensions: {masse.Breite}x{masse.Hoehe}, {masse.Dauer}s (expected 1280x720, 120s)");

    // Without ffmpeg there is no placeholder. Then the run must *say* it -
    // a silent failure would be the fault. (On a machine without ffmpeg
    // the rest cannot be checked; that is a gap of the environment,
    // not of the plugin.)
    var ok = ffmpeg is null
        ? bericht.Schatten == 2 && bericht.Attrappen == 0 && bericht.FehlerZahl == 1
          && bericht.Fehler.Count == 1 && bericht.Fehler[0].Contains("ffmpeg missing", StringComparison.OrdinalIgnoreCase)
          && new FileInfo(a).LinkTarget is not null
        : bericht.Schatten == 2 && bericht.Attrappen == 1 && bericht.FehlerZahl == 0
          && new FileInfo(a).LinkTarget is not null && File.Exists(neu) && new FileInfo(neu).Length > 0
          && masse.Breite == 1280 && masse.Hoehe == 720 && masse.Dauer is >= 118 and <= 122;
    Console.WriteLine(ffmpeg is null
        ? (ok ? "SHADOW OK (without ffmpeg: the error is reported)" : "SHADOW FAIL")
        : (ok ? "SHADOW OK" : "SHADOW FAIL"));
    Directory.Delete(basis, true);
    return ok ? 0 : 1;
}

Console.WriteLine("Naming rules");

// Target names with an id stay intact, the id matters to Jellyfin.
Pruefe(
    "Canonical keeps the tmdbid tag",
    "10 Cloverfield Lane (2016) [tmdbid-333371]",
    Textregeln.Canonical("10 Cloverfield Lane (2016) [tmdbid-333371]"));

// Comparison key: dotted names with release tags and plain names must coincide.
Pruefe(
    "Norm dotted == Norm name",
    Textregeln.Norm("Nordlicht (1994)"),
    Textregeln.Norm("Nordlicht.1994.German.DL.1080p.BluRay"));

Pruefe("Norm dotted", "nordlicht 1994", Textregeln.Norm("Nordlicht.1994.German.DL.1080p.BluRay"));

// Articles must NOT disappear - otherwise 'Der Blade Runner'
// and 'Blade Runner' collide.
Pruefe(
    "Norm keeps articles",
    "der blade runner 1982",
    Textregeln.Norm("Der Blade Runner (1982)"));

// Remakes stay separate.
Pruefe("Norm separates years", "aladdin 1992|aladdin 2019", Textregeln.Norm("Aladdin (1992)") + "|" + Textregeln.Norm("Aladdin (2019)"));

// Numbers in the title are not a year.
Pruefe("Norm keeps 2049", "blade runner 2049 2017", Textregeln.Norm("Blade Runner 2049 (2017)"));

// Umlauts: resolve the accent, do not cut it off.
Pruefe("Norm accents", "pokemon 1999", Textregeln.Norm("Pokémon (1999)"));

// DE without brackets becomes brackets.
Pruefe("Canonical DE", "Irgendein Film (2024)", Textregeln.Canonical("Irgendein Film 2024 DE"));

// A bare number as year gets brackets.
Pruefe("Canonical year at the end", "Heidi (1974)", Textregeln.Canonical("Heidi - 1974"));

// Underscore is a separator.
Pruefe("Canonical underscore", "Die Nackte Kanone (1988)", Textregeln.Canonical("Die_Nackte_Kanone_(1988)"));

// Duplicate year.
Pruefe("Canonical duplicate year", "Titel (2020)", Textregeln.Canonical("Titel 2020 (2020)"));

// Year trap: Jellyfin reads four-digit ids from 1900 to 2099 as a year.
Pruefe("Year trap 1930", "true", Textregeln.JahresFalle("1930").ToString().ToLowerInvariant());
Pruefe("Year trap 10009", "false", Textregeln.JahresFalle("10009").ToString().ToLowerInvariant());
Pruefe("Year trap 1800", "false", Textregeln.JahresFalle("1800").ToString().ToLowerInvariant());

// A film that is really a series (a miniseries sold as one film): the folder
// carries no episode numbers, yet the title has to become S01E01 - otherwise
// the switch would find the right id and the merge would still write nothing.
var esGruppe = new Gruppe
{
    Schluessel = "tmdb:1961",
    Anzeigename = "Stephen King's Es (1990) [tmdbid-1961]",
    TmdbId = "1961"
};
esGruppe.Beitraege["1 Probe"] = new Quellenbeitrag
{
    Wurzel = "/probe/1",
    Ordner = "/probe/1/Es (1990)",
    Datei = "/probe/1/Es (1990)/Es (1990).mkv"
};
var esQuellen = new List<Quelle>
{
    new Quelle { Kennung = "1 Probe", Art = Quellenart.Lokal, Rang = 0, Pfad = "/probe/1" }
};
var esGruppen = new Dictionary<string, Gruppe>(StringComparer.Ordinal) { [esGruppe.Schluessel] = esGruppe };
var esPlan = Planen.Serien(esGruppen, "/ziel-serien", esQuellen, folgenErfinden: true);
Pruefe("Film as series: one episode", "1", esPlan.Count.ToString());
Pruefe("Film as series: season folder", "true", esPlan.Any(e => e.Ziel.Contains("Season 01")).ToString().ToLowerInvariant());
Pruefe("Film as series: episode name", "true", esPlan.Any(e => e.Ziel.Contains("S01E01")).ToString().ToLowerInvariant());
Pruefe("Film as series: marked as series", "true", esPlan.All(e => e.IstSerie).ToString().ToLowerInvariant());
Pruefe("Film as series: opt-in only", "0",
    Planen.Serien(esGruppen, "/ziel-serien", esQuellen, folgenErfinden: false).Count.ToString());

// Names: do not duplicate suffixes, rearrange the trap.
Pruefe("Name without suffix", "Horizon", Textregeln.TitelOhneAnhang("Horizon (2024) [tmdbid-1377119]"));
Pruefe(
    "Name de-duplicated",
    "Horizon (2024) [tmdbid-1377119]",
    Textregeln.NameNormalisieren("Horizon (2024) [tmdbid-1377119] (2024) [tmdbid-1377119]"));
Pruefe(
    "Name trap rearranged",
    "The Amazing Spider-Man [tmdbid-1930] (2012)",
    Textregeln.NameNormalisieren("The Amazing Spider-Man (2012) [tmdbid-1930]"));
Pruefe(
    "Name five digits stays",
    "Ratatouille (2007) [tmdbid-10009]",
    Textregeln.NameNormalisieren("Ratatouille (2007) [tmdbid-10009]"));

// Target name: with a trap the id comes before the year.
Pruefe(
    "TargetName trap",
    "The Amazing Spider-Man [tmdbid-1930] (2012)",
    Scannen.ZielName(
        "1930",
        "The Amazing Spider-Man (2012)",
        new TmdbIndex(),
        new Dictionary<string, (string, string?)> { ["1930"] = ("The Amazing Spider-Man", "2012") }));

// TVDB: entries without a TMDB counterpart keep their own id.
Pruefe(
    "TargetName TVDB",
    "Die wilde Welt des Winters (2020) [tvdbid-407966]",
    Scannen.ZielName(
        "tvdb:407966",
        "Die wilde Welt des Winters (2020) [tmdbid-0]",
        new TmdbIndex(),
        new Dictionary<string, (string, string?)> { ["tvdb:407966"] = ("Die wilde Welt des Winters", "2020") }));

Pruefe("Norm keeps tvdbid", "Titel (2020) [tvdbid-407966]", Textregeln.NameNormalisieren("Titel [tvdbid-407966] (2020)"));
Pruefe("Title without tvdbid", "Titel", Textregeln.TitelOhneAnhang("Titel [tvdbid-407966]"));

// Series: season/episode.
var se = Textregeln.SeKey("Dark - S01E02 - 1 Lokal.mkv");
Pruefe("SeKey", "1/2", se is null ? "null" : $"{se.Value.Staffel}/{se.Value.Folgen[0]}");

var doppel = Textregeln.SeKey("Serie S02E05E06.mkv");
Pruefe("SeKey double episode", "2/5/6", doppel is null ? "null" : $"{doppel.Value.Staffel}/{string.Join("/", doppel.Value.Folgen)}");

Pruefe("SeriesKey with an id", "tmdb:70523", Textregeln.SerienKey("Dark (2017) [tmdbid-70523]"));

Pruefe("SeriesKey without an id", "dark#2017", Textregeln.SerienKey("Dark (2017)"));

Pruefe("TmdbAusNamen", "333371", Textregeln.TmdbAusNamen("10 Cloverfield Lane (2016) [tmdbid-333371]") ?? "null");

Pruefe("IstSample", "true", Textregeln.IstSample("/x/Film/sample/Film.mkv").ToString().ToLowerInvariant());

// Mapping status: an id makes a title mapped, even when the library knows no
// title for it. Source-only titles carry their id in the folder name and must
// leave "without mapping" - otherwise every such title stays there for good.
Pruefe("status with an id", "zugeordnet", Zuordnungsstatus.StatusFuer("tmdb:68315"));
Pruefe("status without an id", "ohne-kennung", Zuordnungsstatus.StatusFuer(""));
Pruefe("source-only title warns about the missing title", "True",
    Zuordnungsstatus.OhneInfoFuer("tmdb:68315", false, false).ToString());
Pruefe("library title needs no warning", "False",
    Zuordnungsstatus.OhneInfoFuer("tmdb:68315", false, true).ToString());
Pruefe("foreign tag needs no title", "False",
    Zuordnungsstatus.OhneInfoFuer("tvdb:4711", true, false).ToString());
Pruefe("no id means no title warning", "False",
    Zuordnungsstatus.OhneInfoFuer("", false, false).ToString());

// ---- Run against real folders ----
var quellen = new List<string>();
string? ziel = null;
string? dump = null;
string? indexPfad = null;
foreach (var a in args)
{
    if (a.StartsWith("--ziel=", StringComparison.Ordinal))
    {
        ziel = a["--ziel=".Length..];
    }
    else if (a.StartsWith("--dump=", StringComparison.Ordinal))
    {
        dump = a["--dump=".Length..];
    }
    else if (a.StartsWith("--index=", StringComparison.Ordinal))
    {
        indexPfad = a["--index=".Length..];
    }
    else
    {
        quellen.Add(a);
    }
}

if (quellen.Count == 0)
{
    Console.WriteLine(fehler == 0 ? "\nAll rules pass." : $"\n{fehler} deviations.");
    return fehler == 0 ? 0 : 1;
}

Console.WriteLine("\nRun against folders");
var bestand = new List<(string, string, int?)>();
if (indexPfad is not null && File.Exists(indexPfad))
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPfad));
    foreach (var e in doc.RootElement.EnumerateObject())
    {
        var titel = e.Value.TryGetProperty("titel", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(titel))
        {
            continue;
        }

        int? jahr = e.Value.TryGetProperty("jahr", out var j) && j.ValueKind == System.Text.Json.JsonValueKind.Number
            ? j.GetInt32()
            : null;
        bestand.Add((e.Name, titel, jahr));
    }
}

Console.WriteLine($"Title index: {bestand.Count} entries");
var index = TmdbIndex.AusBestand(bestand);
var leer = new Dictionary<string, (string, string?)>();

var qliste = quellen
    .Select((p, i) => new Quelle
    {
        Kennung = $"{i + 1} Probe",
        Art = p.Contains("nas", StringComparison.OrdinalIgnoreCase) ? Quellenart.Nas : Quellenart.Lokal,
        Rang = i,
        Pfad = p
    })
    .ToList();

var filme = Scannen.Filme(qliste, index, leer, NullLogger.Instance);
var planFilme = Planen.Filme(filme, ziel ?? "/tmp/ziel-filme", qliste);
Console.WriteLine($"Movies:  {filme.Count} titles, {planFilme.Count} entries");

var serien = Scannen.Serien(qliste, index, leer, NullLogger.Instance);
var planSerien = Planen.Serien(serien, ziel ?? "/tmp/ziel-serien", qliste);
Console.WriteLine($"Series: {serien.Count} titles, {planSerien.Count} entries");

foreach (var p in planFilme.Take(5))
{
    Console.WriteLine($"  {p.Ziel}  <-  {p.Quelle}");
}

void Uebersicht(string was, List<PlanEintrag> plan)
{
    foreach (var gruppe in plan.GroupBy(p => p.Kennung).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {was} {gruppe.Key}: {gruppe.Count()}");
    }
}

Uebersicht("Film", planFilme);
Uebersicht("Serie", planSerien);

if (dump is not null)
{
    using var w = new StreamWriter(dump);
    foreach (var p in planFilme.Concat(planSerien))
    {
        w.WriteLine($"{p.Kennung};{p.Ziel};{p.Quelle}");
    }
}

var doppelt = planFilme.GroupBy(p => p.Ziel, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
Console.WriteLine($"Duplicate target paths: {doppelt.Count}");
foreach (var d in doppelt.Take(5))
{
    fehler++;
    Console.WriteLine($"  FAIL duplicate: {d.Key}");
}

Console.WriteLine(fehler == 0 ? "\nAll green." : $"\n{fehler} deviations.");
return fehler == 0 ? 0 : 1;
