# Identity assets

`air-mark.svg` is the icon (square, legible at 32 px, `currentColor` so it follows the page),
`air-banner.svg` is the README banner (1200×300), `air-social.svg` is the wider card for social
previews (1280×640). Palette: `#10241F` field, `#F3F7F2` text, `#2B806C` accent, `#123D35` for text
on white.

Provenance: drawn for this project by an external model service at the owner's request, as original
vector markup — no stock art, no third-party marks, no fonts fetched at render time (the banner uses the
system font stack, so glyph shapes vary slightly by platform). MIT like the rest of the repository.

One caveat that is not a defect: GitHub's social-preview setting wants an uploaded PNG or JPEG, so
committing `air-social.svg` does not by itself set the card for the repository. It has to be exported
and pasted into the repository settings.

## Avatar-ready mark

`air-mark-avatar.svg` is the same original mark art with the field and stroke colours fixed explicitly
(`#10241F` field, `#F3F7F2` strokes) and reframed into an 80-unit canvas so a circular crop — which is what
GitHub does to an organisation picture — loses none of the three route dots. It exists because
`air-mark.svg` strokes in `currentColor` and declares no field: rendered on its own, which is what an avatar
upload does, it is black on transparent and invisible on a dark frame.

**Rule: never upload `air-mark.svg` as an avatar or any other standalone image.** It is correct only where a
page supplies the colour (the README and the banner set it); anything that renders it without that context
gets an invisible mark. Use `air-mark-avatar.svg` for any standalone use. Provenance: recolour and reframe of
the same generated vector artwork, no new artwork; MIT like the rest of the repository.

## Rasters, and the font that actually resolved

These are rendered exports, not sources. If you want the artwork, edit the SVG; the PNGs are regenerated, and
the regeneration command is below.

| File | Rendered from | Renderer | Font resolved | Size | sha256 |
|---|---|---|---|---|---|
| `air-social-1280x640.png` | `air-social.svg` | resvg 2.6.2 (`@resvg/resvg-js`) | **DejaVu Sans** | 1280×640, 30,842 B | `b02b6d329c39fe046e18fe514d16f4dfb6e280a325f5e96cb24ea0bb2f5b97e9` |
| `air-mark-512.png` | `air-mark-avatar.svg` | resvg 2.6.2 (`@resvg/resvg-js`) | none — no text in this asset | 512×512, 9,935 B | `2e1b5e0286295ed50228d5e46dc66623772fb925befecc2c2a431cd219e820a2` |

The SVGs stay authored against `system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif`, which is
what a browser renders and what makes the banner look native on GitHub. This box has exactly one proportional
sans face, DejaVu Sans, and `system-ui` resolves to nothing here — so the committed raster carries DejaVu Sans
and says so. resvg's own default fallback is DejaVu Sans **Mono**, which is why the export pins the fallback
explicitly. The wordmark was not retraced by hand: a faithful raster with a named fallback beats a redrawn one.
Byte-identical on re-render, so the pipeline is deterministic:

```bash
mkdir -p /tmp/svg-render && cd /tmp/svg-render && npm init -y >/dev/null && npm install @resvg/resvg-js
node /tmp/svg-render/export.js   # font: {defaultFontFamily:'DejaVu Sans', sansSerifFamily:'DejaVu Sans', loadSystemFonts:true}
```
