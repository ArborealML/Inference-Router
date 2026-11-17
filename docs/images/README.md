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
