// Mock for the preview: answers the settings page calls
// with data as the real server delivers it. Case selectable via ?fall=...
(function () {
    'use strict';
    var fall = (location.search.match(/fall=(\w+)/) || [])[1] || 'ok';

var ALTLASTEN = {
    stand: new Date(Date.now() - 47 * 60000).toISOString(),
    bekannt: true,
    gesamt: 4,
    zeilen: [
        { Art: 'Ordner', Pfad: '/media/movies/21 Jump Street (2012) [tmdbid-6468]', Name: '21 Jump Street (2012) [tmdbid-6468]', Kategorie: '/media/movies', Dateien: 3, Bytes: 214, Geaendert: new Date(Date.now() - 26 * 3600000).toISOString() },
        { Art: 'Ordner', Pfad: '/media/series/The Godfather (1972) [tmdbid-238]', Name: 'The Godfather (1972) [tmdbid-238]', Kategorie: '/media/series', Dateien: 12, Bytes: 1840, Geaendert: new Date(Date.now() - 74 * 3600000).toISOString() },
        { Art: 'Datei', Pfad: '/media/series/Knightfall (2017) [tmdbid-73117]/Season 01/Knightfall (2017) [tmdbid-73117] - S01E01 - 2 Stream.nfo', Name: 'Knightfall (2017) [tmdbid-73117] - S01E01 - 2 Stream.nfo', Kategorie: '/media/series', Dateien: 1, Bytes: 2048, Geaendert: new Date(Date.now() - 20 * 3600000).toISOString() },
        { Art: 'Datei', Pfad: '/media/movies/Halloween (1978) [tmdbid-948]/Halloween (1978) [tmdbid-948] - 3 NAS.mkv', Name: 'Halloween (1978) [tmdbid-948] - 3 NAS.mkv', Kategorie: '/media/movies', Dateien: 1, Bytes: 141, Geaendert: new Date(Date.now() - 52 * 3600000).toISOString() }
    ]
};

    var KONFIG = {
        Quellen: [
            { Kennung: '1 Local', Art: 'Lokal', Rang: 1, Pfad: '/mnt/disc1/movies', StandardKategorie: 'Filme', Schatten: false },
            { Kennung: '2 Stream', Art: 'Stream', Rang: 2, Pfad: '/mnt/streamed/movies', StandardKategorie: 'Filme', Schatten: false },
            { Kennung: '3 NAS', Art: 'Nas', Rang: 50, Pfad: '/mnt/nas/movies', StandardKategorie: 'Filme', Schatten: true },
            { Kennung: '1 Local Series', Art: 'Lokal', Rang: 51, Pfad: '/mnt/disc1/series', StandardKategorie: 'Serien', Schatten: false },
            { Kennung: '2 Stream Series', Art: 'Lokal', Rang: 52, Pfad: '/mnt/streamed/series', StandardKategorie: 'Serien', Schatten: false },
            { Kennung: '3 NAS Series', Art: 'Nas', Rang: 53, Pfad: '/mnt/nas/series', StandardKategorie: 'Serien', Schatten: true }
        ],
        Kategorien: [
            { Name: 'Filme', IstSerie: false, Zielpfad: '/srv/merged-filme', Muster: '', Standard: true },
            { Name: 'Serien', IstSerie: true, Zielpfad: '/srv/merged-serien', Muster: '', Standard: true },
            { Name: 'Dokumentationen', IstSerie: false, Zielpfad: '/srv/merged-dokumentation', Muster: 'Doku;Dokumentation', Standard: false }
        ],
        TmdbSchluessel: '',
        OmdbSchluessel: '',
        TmdbSprache: 'de-DE',
        IntervallMinuten: 15,
        SicherungenBehalten: 20,
        NfoSchreiben: true,
        NfoJahrKorrigieren: true,
        Aufraeumen: false,
        PruefIntervallMinuten: 5,
        AufraeumenTage: 30,
        FremdeProzesseBeenden: false,
        NurPlanen: false,
        BeimStart: true
    };

    var ZEILEN = [
        { ordner: 'The Godfather (1972)', quelle: '1 Local', istId: '', istHerkunft: '', istName: 'The Godfather', istJahr: 1972, ziel: '', status: 'ohne-kennung', kategorien: [] },
        { ordner: 'Dune Part Two (2024) 2160p', quelle: '2 Stream', istId: '693134', istHerkunft: 'name', istName: 'Dune: Part Two', istJahr: 2024, ziel: 'Filme/Dune Part Two (2024) [tmdbid-693134]', status: 'zugeordnet', kategorien: ['Filme'] },
        { ordner: 'Tiefsee - Leben im Dunkeln', quelle: '3 Nas', istId: '', istHerkunft: '', istName: 'Tiefsee - Leben im Dunkeln', istJahr: null, ziel: '', status: 'ohne-info', kategorien: ['Dokumentationen'] },
        { ordner: 'Training Day (2002)', quelle: '1 Local', istId: '2034', istHerkunft: 'nfo', istName: 'Training Day', istJahr: 2034, ziel: 'Filme/Training Day [tmdbid-2034] (2002)', status: 'zugeordnet', kategorien: ['Filme'] },
        { ordner: 'Alien 1979 REMASTERED', quelle: '2 Stream', istId: '', istHerkunft: '', istName: 'Alien', istJahr: 1979, ziel: '', status: 'offen', kategorien: [] },
        { ordner: 'Blade Runner 2049 (2017)', quelle: '1 Local', istId: '335984', istHerkunft: 'bibliothek', istName: 'Blade Runner 2049', istJahr: 2017, ziel: 'Filme/Blade Runner 2049 (2017) [tmdbid-335984]', status: 'zugeordnet', kategorien: ['Filme'] },
        { ordner: 'Planet Erde II', quelle: '3 Nas', istId: '', istHerkunft: '', istName: 'Planet Erde II', istJahr: 2016, ziel: '', status: 'ohne-kennung', kategorien: [] },
        { ordner: 'Heat (1995)', quelle: '1 Local', istId: '949', istHerkunft: 'hand', istName: 'Heat', istJahr: 1995, ziel: 'Filme/Heat (1995) [tmdbid-949]', status: 'zugeordnet', kategorien: [] },
        { ordner: 'Chinatown (1974)', quelle: '2 Stream', istId: '', istHerkunft: '', istName: 'Chinatown', istJahr: 1974, ziel: '', status: 'offen', kategorien: [] },
        { ordner: 'Akira (1988)', quelle: '3 Nas', istId: '149', istHerkunft: 'name', istName: 'Akira', istJahr: 1988, ziel: 'Filme/Akira (1988) [tmdbid-149]', status: 'zugeordnet', kategorien: ['Filme'] },
        { ordner: 'Unbekannt 04', quelle: '2 Stream', istId: '', istHerkunft: '', istName: '', istJahr: null, ziel: '', status: 'offen', kategorien: [], fehlt: true },
        { ordner: 'Stalker (1979)', quelle: '1 Local', istId: '1398', istHerkunft: 'nfo', istName: 'Stalker', istJahr: 1979, ziel: 'Filme/Stalker (1979) [tmdbid-1398]', status: 'zugeordnet', kategorien: ['Filme'] }
    ];

    var BESTAND = {
        seite: 1, seiten: 8, gesamt: 387, gefiltert: 96, offen: 31, ohneMedium: 4,
        kategorien: ['Filme', 'Dokumentationen'],
        fremdeKategorien: ['Serien', 'TV-Sendungen'],
        quellen: ['1 Local', '2 Stream', '3 NAS'],
        standardKategorie: 'Filme',
        tmdbSchluessel: false,
        omdbSchluessel: false,
        zeilen: ZEILEN
    };

    var STATUS = {
        ok: { ergebnis: 'ok', laeuft: false, fortschritt: 100, phase: 'fertig', intervall: 15,
              letztesEnde: new Date(Date.now() - 12 * 60000).toISOString(), dauer: 11.9,
              gesetzt: 0, unveraendert: 80054, fehler: 0, meldung: '0 created, 80054 unchanged' },
        warnung: { ergebnis: 'ok', warnung: 'Der Zielordner nimmt keine Verweise auf (Windows ohne Entwicklermodus). Es wurde nichts geschrieben.',
                   laeuft: false, fortschritt: 100, phase: 'fertig', intervall: 15,
                   letztesEnde: new Date(Date.now() - 90 * 60000).toISOString(), dauer: 2.4,
                   gesetzt: 0, unveraendert: 0, fehler: 0, meldung: '' },
        laeuft: { ergebnis: 'laeuft', laeuft: true, fortschritt: 42, phase: 'Verweise setzen', intervall: 15,
                  letzterStart: new Date(Date.now() - 30 * 60000).toISOString(),
                  letztesEnde: null, dauer: 11.9,
                  gesetzt: 128, unveraendert: 79000, fehler: 0, meldung: '' },
        nie: { ergebnis: 'unbekannt', laeuft: false, intervall: 0 }
    };

    var SICHERUNGEN = { eintraege: [
        { name: 'zustand-20261122-105134.json', zeit: new Date(Date.now() - 12 * 60000).toISOString(), groesse: 184320 },
        { name: 'zustand-20261122-103012.json', zeit: new Date(Date.now() - 32 * 60000).toISOString(), groesse: 183808 },
        { name: 'zustand-20261121-221044.json', zeit: new Date(Date.now() - 13 * 3600000).toISOString(), groesse: 181760 }
    ] };

    function P(v, fehler) {
        return { then: function (a, b) { setTimeout(function () { (fehler ? b : a) && (fehler ? b : a)(v); }, 260); return this; } };
    }

    window.ApiClient = {
        getUrl: function (pfad, q) {
            var s = Object.keys(q || {}).map(function (k) { return k + '=' + encodeURIComponent(q[k]); }).join('&');
            return '/' + pfad + (s ? '?' + s : '');
        },
        getPluginConfiguration: function () { return P(JSON.parse(JSON.stringify(KONFIG))); },
        updatePluginConfiguration: function (id, c) { Object.assign(KONFIG, c); return P(undefined); },

        ajax: function (o) {
            var u = o.url || '';
            if (o.type === 'POST' && u.indexOf('Lauf') >= 0) { return P({ nurPlanen: false }); }
            if (o.type === 'POST' && u.indexOf('Aufraeumen/Entfernen') >= 0) {
                var n = (JSON.parse(o.data || '{}').pfade || []).length || 4;
                return P({ entfernt: n, fehler: 0, meldung: n + ' entries moved to "-alt/aufraeumen".' });
            }
            if (u.indexOf('Aufraeumen') >= 0) { return P(ALTLASTEN); }
            if (u.indexOf('Status') >= 0) { return P(STATUS[fall] || STATUS.ok); }
            if (u.indexOf('Sicherungen') >= 0) { return P(SICHERUNGEN); }
            if (u.indexOf('Kennung/Suche') >= 0) { return P({ gefunden: true, titel: 'The Godfather', jahr: 1972, tmdbId: 238, hinweis: 'über Titel gefunden' }); }
            if (u.indexOf('Kennung') >= 0) { return P({ gefunden: true, titel: 'The Godfather', jahr: 1972, tmdbId: '238' }); }
            if (o.type === 'POST' && u.indexOf('Zuordnung') >= 0) { return P({ name: 'The Godfather', jahr: 1972, tmdbId: '238' }); }
            if (o.type === 'POST' && u.indexOf('Kategorie') >= 0) { return P({ kategorien: JSON.parse(o.data || '{}').Kategorien || [] }); }
            if (u.indexOf('Bestand') >= 0) { return P(BESTAND); }
            return P({});
        }
    };
})();
