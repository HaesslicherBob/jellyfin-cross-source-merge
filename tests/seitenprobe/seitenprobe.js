// Page probe: click through configPage.html without Jellyfin.
// Usage: npm install && node seitenprobe.js
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const seite = path.join(__dirname, '..', '..', 'src', 'Jellyfin.Plugin.CrossSourceMerge', 'Configuration', 'configPage.html');
const html = fs.readFileSync(seite, 'utf8');

const attrappe = `
<script>
window.__aufrufe = [];
window.__configAufrufe = 0;
function P(v) {
    return { then: function (a, b) { setTimeout(function () { try { a && a(v); } catch (e) { window.__fehler = (window.__fehler || []).concat(String(e)); } }, 0); return this; } };
}
function KONFIG() {
    return { Quellen: [{ Kennung: '1 Lokal', Art: 'Lokal', Rang: 10, Pfad: '/q', StandardKategorie: '' }],
             Kategorien: [{ Name: 'Filme', IstSerie: false, Zielpfad: '/z/filme', Standard: true },
                          { Name: 'Dokumentationen', IstSerie: false, Zielpfad: '/z/doku' },
                          { Name: 'Serien', IstSerie: true, Zielpfad: '/z/serien', Standard: true }],
             TmdbSprache: 'de-DE', IntervallMinuten: 15, SicherungenBehalten: 20 };
}
var BESTAND = {
    seiten: 1, gesamt: 2,
    kategorien: ['Filme', 'Dokumentationen'], standardKategorie: 'Filme', fremdeStandard: 'Serien',
    zeilen: [
        { art: 'movie', ordner: 'Irgendein Film (2020)', quelle: '1 Lokal', pfad: '/q/Irgendein Film (2020)',
          istId: '123', istHerkunft: 'name', istName: 'Irgendein Film', istJahr: 2020,
          ziel: 'filme/Irgendein Film (2020)', status: 'zugeordnet', kategorien: [] },
        { art: 'movie', ordner: 'Tiefsee Doku (2019)', quelle: '1 Lokal', pfad: '/q/Tiefsee Doku (2019)',
          istId: '456', istHerkunft: 'nfo', istName: 'Tiefsee Doku', istJahr: 2019,
          ziel: 'filme/Tiefsee Doku (2019)', status: 'zugeordnet', kategorien: ['Filme', 'Dokumentationen'] }
    ]
};
window.ApiClient = {
    getUrl: function (pfad, q) {
        var s = Object.keys(q || {}).map(function (k) { return k + '=' + encodeURIComponent(q[k]); }).join('&');
        return '/fake/' + pfad + (s ? '?' + s : '');
    },
    getPluginConfiguration: function () { window.__configAufrufe++; return P(KONFIG()); },
    updatePluginConfiguration: function (id, c) { window.__aufrufe.push({ art: 'config', c: c }); return P(undefined); },
    ajax: function (o) {
        window.__aufrufe.push({ art: o.type || 'GET', url: o.url, koerper: o.data ? JSON.parse(o.data) : null });
        if (o.type === 'POST') {
            var k = o.data ? JSON.parse(o.data).Kategorien : [];
            return P({ ordner: 'x', kategorien: k });
        }
        if (o.url.indexOf('Bestand') >= 0) { return P(BESTAND); }
        if (o.url.indexOf('Status') >= 0) { return P({ ergebnis: 'ok', fortschritt: 100, phase: 'fertig', intervall: 15 }); }
        return P({});
    }
};
window.Dashboard = { alert: function () {}, processPluginConfigurationUpdateResult: function () {} };
</script>
`;

let fehler = 0;
function pruefe(was, soll, ist) {
    const ok = String(soll) === String(ist);
    if (!ok) fehler++;
    console.log((ok ? '  ok   ' : '  FAIL ') + was + (ok ? '' : '\n       expected: ' + soll + '\n       actual:   ' + ist));
}
const warte = (ms) => new Promise(r => setTimeout(r, ms));

(async () => {
    // Stylesheet sanity: if the palette is not defined, the whole page falls
    // back to inherited grey and the marks turn invisible.
    const stil = (html.match(/<style[^>]*>([\s\S]*?)<\/style>/) || [])[1] || '';
    let offen = 0, zerbrochen = false;
    for (const m of stil.matchAll(/\/\*|\*\//g)) {
        if (m[0] === '/*') { offen++; } else { offen--; if (offen < 0) { zerbrochen = true; } }
    }
    pruefe('styles have balanced comments', 'true', String(offen === 0 && !zerbrochen));
    const definiert = new Set([...stil.matchAll(/(--csm-[a-z0-9-]+)\s*:/g)].map(m => m[1]));
    const benutzt = new Set([...stil.matchAll(/var\(\s*(--csm-[a-z0-9-]+)/g)].map(m => m[1]));
    const fehlend = [...benutzt].filter(v => !definiert.has(v));
    pruefe('every --csm variable is defined' + (fehlend.length ? ' (missing: ' + fehlend.join(', ') + ')' : ''), '0', fehlend.length);

    const dom = new JSDOM(html.replace('<head>', '<head>' + attrappe), {
        runScripts: 'dangerously', pretendToBeVisual: true, url: 'http://127.0.0.1/fake'
    });
    const w = dom.window;
    const d = w.document;
    await warte(300);

    const tab = (name) => [...d.querySelectorAll('.csm-tab')].find(t => t.getAttribute('data-tab') === name);
    tab('zuordnungen').click();
    await warte(400);

    const zeilen = d.querySelectorAll('#bestandBody tr');
    pruefe('two rows rendered', 2, zeilen.length);

    const erste = zeilen[0];
    pruefe('toggle per category and default', 'Filme|Dokumentationen|Standard',
        [...erste.querySelectorAll('.csm-kat')].map(b => b.textContent).join('|'));
    pruefe('nothing selected without a mapping', 'false|false',
        [...erste.querySelectorAll('.csm-kat:not(.std)')].map(b => b.getAttribute('aria-pressed')).join('|'));
    pruefe('status line names the default', '\u2192 Standard (Filme)', erste.querySelector('.csm-kat-stand').textContent);

    const zweite = zeilen[1];
    pruefe('saved multi-selection is visible', 'true|true',
        [...zweite.querySelectorAll('.csm-kat:not(.std)')].map(b => b.getAttribute('aria-pressed')).join('|'));
    pruefe('status line shows both', '\u2192 Filme, Dokumentationen', zweite.querySelector('.csm-kat-stand').textContent);

    // A film that is really a series: the switch changes the search kind and
    // files the title into the series tree in the same step - without it the
    // wrong id would keep winning and the film would never reach its tree.
    const schalter = zweite.querySelector('.csm-serie input');
    pruefe('series switch on a film row', 'true', !!schalter);
    w.__aufrufe.length = 0;
    schalter.click();
    await warte(200);
    const suche = w.__aufrufe.filter(a => /Kennung\/Suche/.test(a.url)).pop();
    pruefe('switch searches as series', 'true', /serie=true/.test(suche.url));

    w.__aufrufe.length = 0;
    [...zweite.querySelectorAll('button')].find(b => b.textContent.trim() === 'Apply').click();
    await warte(300);
    const zuordnung = w.__aufrufe.filter(a => a.art === 'POST' && /Zuordnung/.test(a.url))[0];
    pruefe('apply keeps the film namespace', 'movie', zuordnung.koerper.Art);
    pruefe('apply marks it as a series', 'true', String(zuordnung.koerper.Serie));
    const katPost = w.__aufrufe.filter(a => a.art === 'POST' && /Kategorie/.test(a.url))[0];
    pruefe('apply files it into the series tree', 'true',
        !!katPost && katPost.koerper.Kategorien.indexOf('Serien') >= 0);

    w.__aufrufe.length = 0;
    [...erste.querySelectorAll('.csm-kat')].find(b => b.textContent === 'Dokumentationen').click();
    await warte(200);
    let post = w.__aufrufe.find(a => a.art === 'POST');
    pruefe('click sends POST', 'true', !!post);
    pruefe('one category', 'Dokumentationen', post.koerper.Kategorien.join('|'));

    [...erste.querySelectorAll('.csm-kat')].find(b => b.textContent === 'Filme').click();
    await warte(200);
    let posts = w.__aufrufe.filter(a => a.art === 'POST');
    pruefe('second click sends both', 'Dokumentationen,Filme', posts[posts.length - 1].koerper.Kategorien.join(','));

    erste.querySelector('.csm-kat.std').click();
    await warte(200);
    posts = w.__aufrufe.filter(a => a.art === 'POST');
    pruefe('default sends an empty list', '', posts[posts.length - 1].koerper.Kategorien.join(','));

    // Configuration: a missing target path must not vanish silently
    tab('quellen').click();
    await warte(100);
    const pfad = d.querySelector('#kategorienBody tr .k-pfad');
    pfad.value = '';
    w.__aufrufe.length = 0;
    const speichern = [...d.querySelectorAll('button')].find(b => b.textContent.trim() === 'Save' && b.closest('[data-tab="quellen"]'));
    speichern.click();
    await warte(300);
    pruefe('no save without a path', 'false', !!w.__aufrufe.find(a => a.art === 'config'));
    pruefe('message names the reason', 'true',
        /target path/i.test([...d.querySelectorAll('.csm-meldung')].map(m => m.textContent).join('|')));
    pruefe('row is marked', 'true', d.querySelector('#kategorienBody tr').className.indexOf('csm-fehlerzeile') >= 0);

    pfad.value = '/z/filme';
    w.__aufrufe.length = 0;
    const vorher = w.__configAufrufe;
    speichern.click();
    await warte(400);
    pruefe('with a path it is saved', 'true', !!w.__aufrufe.find(a => a.art === 'config'));
    pruefe('confirmation appears despite 204', 'true',
        /Saved/.test([...d.querySelectorAll('.csm-meldung')].map(m => m.textContent).join('|')));
    pruefe('configuration is reloaded', 'true', w.__configAufrufe > vorher);
    pruefe('no exception error in the page', 'undefined', String(w.__fehler));

    // Refresh must bypass the cache
    tab('zuordnungen').click();
    await warte(300);
    w.__aufrufe.length = 0;
    d.getElementById('btnNeu').click();
    await warte(300);
    const bestand = w.__aufrufe.filter(a => /Bestand/.test(a.url)).pop();
    pruefe('refresh requests erneuern', 'true', /erneuern=true/.test(bestand.url));

    console.log(fehler === 0 ? 'PAGE OK' : 'PAGE FAIL (' + fehler + ')');
    process.exit(fehler === 0 ? 0 : 1);
})().catch(e => { console.error('Exception:', e); process.exit(2); });
