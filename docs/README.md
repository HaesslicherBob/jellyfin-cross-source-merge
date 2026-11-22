# Images

These are rendered mockups, not screenshots of a running library. They exist so the
README can show the idea without publishing somebody's media files.

- titles, TMDB ids and episode names are invented (`Northern Light (2024)`, id `118742`)
- the Jellyfin page and the settings table are drawn after the real UI, with the
  labels translated to English for readability
- the folder layouts follow the plugin's naming rule, only shortened with `…`

## Regenerating

The SVG sources live in `src/`, next to the small render script:

```bash
npm install @resvg/resvg-js      # once, in this folder
node src/render.js src/banner.svg docs/banner.png 2
```

`render.js` expects TTFs in `fonts/` (Inter and JetBrains Mono are not vendored —
drop `Inter-*.ttf` and `JetBrainsMono-*.ttf` next to it, or point the loader at
your own). The script renders at 2× and keeps the background transparent.

| File | Used in |
|---|---|
| `banner.png` | top of the README |
| `how-it-works.png` | *What it does* — merge of two source layouts |
| `jellyfin-versions.png` | *What it looks like in Jellyfin* |
| `shadow-library.png` | *Shadow library* — plain link vs. placeholder |
| `settings-sources.png` | *The settings page* — sources table |
