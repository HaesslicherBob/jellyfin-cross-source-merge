#!/usr/bin/env python3
"""Builds a standalone preview from configPage.html.

The Jellyfin classes (emby-button, detailTable, fieldDescription ...) are
missing in a plain browser; this puts an imitation of that UI around the page,
plus the mock that answers the API. Result: tests/vorschau/vorschau.html

Run:   python3 tests/vorschau/bauen.py
Open:  tests/vorschau/vorschau.html?fall=ok|warnung|laeuft|nie&tab=zuordnungen&thema=hell
"""
from pathlib import Path

hier = Path(__file__).parent
seite = hier.parent.parent / 'src/Jellyfin.Plugin.CrossSourceMerge/Configuration/configPage.html'
html = seite.read_text(encoding='utf-8')

# The page style block now lives in the page itself (in the root div),
# no longer in the head - it comes with the markup below. Earlier we
# copied it into the head by hand; that was an imitation of the
# Jellyfin UI that does not exist like that.

kopf = '''<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Cross-Source Merge - preview</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="stylesheet" href="jellyfin-skin.css">
<script>
// Theme from the URL so light and dark can be compared.
document.addEventListener('DOMContentLoaded', function () {
    if (/thema=hell/.test(location.search)) { document.body.className = 'hell'; }
});
</script>
<script src="attrappe.js"></script>
</head>
<body>
<div class="jf-kopf">
    <button class="jf-zurueck" title="Back">&larr;</button>
    <h1>Cross-Source Merge</h1>
    <span class="jf-hinweis">Preview without a server &middot; fall=ok|warnung|laeuft|nie &middot; thema=hell</span>
</div>
<div class="page"><div class="content-primary">
'''

fuss = '''
</div></div>
<script>
// Preselect the tab from the URL so every tab can be photographed on its own.
document.addEventListener('DOMContentLoaded', function () {
    var m = location.search.match(/tab=(\\w+)/);
    if (!m) { return; }
    setTimeout(function () {
        var b = document.querySelector('.csm-tab[data-tab="' + m[1] + '"]');
        if (b) { b.click(); }
    }, 300);
});
</script>
</body>
</html>
'''

# From the page root to the end: take markup and page script,
# head and foot come from here.
start = html.index('<div id="CrossSourceMergeConfigPage"')
inhalt = html[start:html.rindex('</body>')]
(hier / 'vorschau.html').write_text(kopf + inhalt + fuss, encoding='utf-8')
print('written:', hier / 'vorschau.html')
