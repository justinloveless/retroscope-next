# Third-party material

Everything below ships in the web client under `web/assets/`.

## m6x11plus (`web/assets/m6x11plus.woff2`)

- **Author:** Daniel Linssen
- **Source:** https://managore.itch.io/m6x11 (the page offers both m6x11 and the extended m6x11plus)
- **Terms:** free to use with attribution. The page asks for the credit "a font by Daniel Linssen", and the font's own name table carries the licence string "Free to use with attribution".
- **Licence page checked on 2026-10-20.** The m6x11plus download sits on the same page as m6x11, under the same terms.
- **Credit line for the app's about/footer:** "m6x11plus, a font by Daniel Linssen".
- **Changes:** subset to Latin (U+0020–007E, U+00A0–00FF) and converted to WOFF2. Name table kept. The source file is the original app's `src/fonts/Neotro.ttf`, which is m6x11plus under another file name.

## Lucide icons (`web/assets/icons.svg`)

- **Source:** lucide-static 1.49.0 (npm), https://lucide.dev
- **Changes:** a subset of the icons, repacked as `<symbol>` elements by `tools/make-sprite.mjs`.

```
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

Some Lucide icons come from the Feather project. Among those in the subset are `arrow-left`, `arrow-right`,
`calendar`, `check`, `chevron-*`, `circle`, `clock`, `corner-up-left`, `crosshair`, `download`, `external-link`,
`hash`, `info`, `link`, `link-2`, `lock`, `log-in`, `log-out`, `moon`, `music`, `plus`, `power`, `search`, `upload` and `x`:

```
The MIT License (MIT)

Copyright (c) 2013-present Cole Bemis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Card art (`web/assets/cards/*.png`, `web/assets/cards.png`)

The nine 71×95 pixel-art cards come from the original Retroscope repository (`src/assets/Card_*.png`, added in
commit `100cb90` "Integrate Neotro"). That repository records no outside author or licence for them. Their origin
is **unconfirmed**: until the project owner confirms the art is the project's own, treat it as unverified and do not
release. If it comes from somewhere else, record the source and terms here.

## Retro timer music

`music.mp3` is not shipped. The original app's default track lived only in a Supabase storage bucket
(`retro-audio`), and neither the bucket nor the repository records where the track came from or under what
licence. Without a licence that permits redistribution, it stays out (§0.1).
