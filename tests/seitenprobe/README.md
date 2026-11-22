# Seitenprobe

Prueft die Konfigurationsseite ohne Jellyfin: `configPage.html` wird geladen,
`ApiClient` und `Dashboard` werden durch eine Attrappe ersetzt, danach klickt
das Skript wie ein Benutzer.

```bash
cd tests/seitenprobe
npm install
node seitenprobe.js
```

Geprueft wird unter anderem:

* the category switches per row (several at once, standard clears),
* that saving without a target path does **not** silently disappear,
* that the confirmation appears even on `204 No Content`,
* that **Refresh** bypasses the cache with `erneuern=true`.

Die Attrappe antwortet absichtlich so wie Jellyfin: `updatePluginConfiguration`
liefert keinen Inhalt.
