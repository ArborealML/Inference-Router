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
| `air-social-1280x640.png` | `air-social.svg` | resvg 2.6.2 (`@resvg/resvg-js`) | **DejaVu Sans** | 1280×640, 38,150 B | `cee3237b7f980d12259d6aad082a9473f12214115b36e0239d85c8147ec98f09` |
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

## The legibility floor on the social card, and what it is proven against

The card was redrawn once. The first version rendered at 1280×640 with capital heights of **109, 29 and 20 px**
for its three lines: the wordmark was fine and the two lines under it were not something you could read in a
link preview or a mobile preview column. The current artwork keeps the same three lines and the same mark, moves
the mark group left (scale 0.71) and the divider from `x=490` to `x=300`, and widens the text column to
`320..1206`; the same measurement now gives **109, 48 and 42 px**. Nothing was accepted on the strength of a
font size in the markup — the numbers come out of the rendered pixels.

How they are measured, so the claim can be re-run: split the raster into horizontal bands of inked rows in the
text column, take a band's cap height as its first inked row to the last row still carrying at least 25% of the
band's peak ink (a descender tail — the `g` in "routing" — is a couple of thin strokes and would otherwise
inflate the number), and require **at least 40 px**. Alongside it: no ink at all in a 3 px border ring, checked
both against the field colour and against *any* colour so a clipped accent in `#2B806C` (luminance ≈ 108) cannot
hide as "no ink"; 79 px of right margin to the rightmost ink; and all three bands still carrying ink after a
downscale to 640×320, which is what a dense preview column actually shows.

One limit worth stating: the floor is proven for the face that resolves **here**. This box renders the declared
system font stack as DejaVu Sans, whose capital height runs about 0.73 em, so the 56 px tagline lands at 42 px.
A face with a smaller cap ratio — some system UI faces are nearer 0.70 or below — would pull that line under the
floor. If the card is ever rendered on another machine, re-run the measurement on the raster it produces rather
than trusting the `font-size` attribute.
