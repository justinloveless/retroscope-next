# UX8 decisions: Jira descriptions keep their ADF styling

The owner asked for this unit: "The issue description needs to be able to display all of the styling of the
original ADF text from Jira. That includes all of the images, styling, the colored blocks, tables, etcetera.
However, do NOT use their javascript library. It is extremely heavy and slow."

UX8 changes `src/Retroscope.Core/{Adf,Markup}`, `web/src/markup/{render,doc,editor,allow,jira}.js`,
`web/src/poker/issue-drawer.js`, `web/styles/jira.css`, `web/build.mjs` (`ROUTE_STYLES`), the icon sprite, the ADF and
markup tests, and the fake Jira fixtures. It revises U6 decisions 3, 6 and 8 and closes the `jira.css` follow-up in
`docs/followups/after-wave5.md` (U33 decision 17).

## Decisions

1. **The server is still the only allow-list.** Raw ADF never reaches the browser. `AdfReader` keeps the presentation
   it used to drop and emits it as new display-only Doc kinds and optional attributes. `DocJson` writes them, and the
   client re-checks every field before it renders. No Atlassian package is loaded, and none is a dependency.
2. **Display-only means never editable.** The new kinds and attributes appear only in islands, comments and non-editable
   descriptions. `AdfLossiness` already islands every top-level block that carries colour, alignment, a link title or
   a non-text inline node, so the markup grammar, `MarkupParser`, `MarkupToAdf` and the editor do not change. Three
   guards keep it that way:
   - `MarkupWriter` and `InlineWriter` throw for a paragraph or heading with a format and for a titled link, as they
     already did for panels.
   - `DocKinds.DisallowedIn` (Core tests) treats a block format and a link title as kinds of their own.
   - `AdfFixtureTests.Blocks_outside_islands_hold_only_markup_kinds` checks every description fixture.
3. **What moved from "dropped" to "rendered":**

   | ADF | Before (U6) | Now (Doc wire shape) |
   |---|---|---|
   | `textColor`, `backgroundColor` marks | dropped | `fg{color}`, `bg{color}` |
   | `subsup` | dropped | `sub`, `sup` |
   | `alignment`, `indentation` on paragraph and heading | dropped | `p`/`h` `align` (`center`/`end`), `indent` (1–6) |
   | link `title` | dropped | `a.title` |
   | panel `tip`, `custom`, `panelColor`, `panelIconText` | success / info | `panel.kind` `tip`/`custom`, `color`, `icon` |
   | table `layout`, `width`, `isNumberColumnEnabled` | dropped | `table.layout`, `width`, `numbered` |
   | cell `colspan`, `rowspan`, `colwidth`, `background` | dropped | `th`/`td` `colspan`, `rowspan`, `colwidth`, `bg` |
   | nested `taskList` | flattened | `tasks` inside `tasks` |
   | `decisionList`/`decisionItem` | bullet list | `decisions`/`decision{done}` |
   | `layoutSection`/`layoutColumn` | flattened | `layout`/`col{width}` |
   | `mediaSingle` `layout`, `width`, `widthType`, caption | image paragraph + caption paragraph | `media{layout, width, unit, caption}` |
   | `mediaGroup` | image paragraph | `files` |
   | media `width`/`height` | dropped | `attachment.w`/`h` (the `img` width/height, for the aspect ratio) |
   | non-image attachment | "`name` (open in Jira)" text | `file{id, name}` chip |
   | `mention` | `@Name` text | `user{v}` pill |
   | `date` | `yyyy-MM-dd` text | `date{v}` lozenge, formatted by the client for the UTC day |
   | `status` `style: "bold"` | dropped | `status.bold` |
   | `inlineCard`/`blockCard`/`embedCard` | link with the URL as text | `card{href, block?}` |

   `expand`/`nestedExpand`, `codeBlock` languages, `rule`, `blockquote`, headings and `hardBreak` kept their Doc shape
   and now get Jira-like styling.
4. **Colours.**
   - **Validation.** A colour must be a strict `#rrggbb`. Anything else is dropped on the server (`AdfColor`) and again
     on the client (`isDocColor`), because the value reaches the CSSOM. The hostile corpus covers `url()`,
     `expression()`, `var()`, CSS injection and wrong lengths.
   - **Palette.** The Jira editor palette (`@atlaskit/editor-palette` 3.5.80, read from unpkg on 2026-10-22) maps each
     hex to an Atlassian design token. The server sends the token's name instead (`blue-bolder`, `red-subtler`, `text`,
     `surface`…). `jira.css` gives each name its light and dark value from `@atlaskit/tokens` 20.7.0 (`atlassian-light`
     and `atlassian-dark`, read the same day), so palette text, highlights, cell colours and custom panels follow the
     theme as they do in Jira. Any other valid hex passes through and shows as written in both themes, as Jira shows it.
   - **The text and background palettes differ.** `#FFFFFF` is `inverse` as text and `surface` as a background. The
     "subtler-hovered" background shades map to `subtler`, the nearest token `jira.css` carries.
   - **Deviation from the brief.** The brief asked for theme-aware classes. The client sets
     `color: var(--jc-<token>)` through `style.setProperty` instead. It is the same theme switch without about 80
     generated classes, and it needs no style attribute strings: CSP `style-src 'self'` allows CSSOM writes.
5. **Panels, lozenges and icons follow Jira's design tokens.**
   - **Panels.** info, note, tip/success, warning and error use Jira's background and icon roles. Their icons come from
     the local Lucide sprite: `info`, `clipboard-list`, `lightbulb`, `circle-check`, `triangle-alert` and `circle-x`.
     `lightbulb`, `paperclip` (file chips) and `split` (decisions) were added to the sprite.
   - **Custom panels.** A custom panel shows its emoji text. Without `panelIconText` it shows no icon: a `:shortName:`
     icon would read as noise.
   - **Status lozenges.** They use Jira's subtle look by default and the bold look for `style: "bold"`. The ADF schema
     types `style` as a free string. That `"bold"` is the value Jira writes is **(unverified)**: none of the probed
     issues had a status node.
6. **Tables.**
   - **Spans and colours.** Spans are capped at 64 and widths at 10,000 px on both sides, and cell colours follow
     decision 4.
   - **Widths.** When the first row's cells give a width for every column, the table gets a `colgroup` and a fixed pixel
     width, as Jira lays it out. Otherwise it sizes itself.
   - **Number column.** It adds a 42 px first column that numbers the rows that are not all header cells, as Jira does.
   - **Scrolling.** The table sits in `.table-wrap` (`overflow-x: auto`) and `.jira-prose` gets `min-width: 0`.
     Without that, a wide table widened the whole description past the centre issue panel's glass.
7. **Media.**
   - **Images.** They still load only through the same-origin attachment proxy, after render.js's same-origin `src`
     check. `figure.media` carries the layout class and the width (percent or pixels, capped at 100 % of the column).
   - **Layouts.** `wrap-left`/`wrap-right` float only from 40rem up, so phones get the image above the text.
     `wide`/`full-width` take the column's width.
   - **Files.** A non-image attachment is a file chip that opens the issue in Jira (`https://<siteHost>/browse/<KEY>`).
     The proxy serves images only, and a direct attachment URL would need Jira's own session.
   - **External media** stay a link, because CSP `img-src 'self' data: blob:` forbids remote images.
8. **Media id to attachment id: checked against real issues.** U19 left this unverified. On 2026-10-22, 15 embedded
   images across 6 real Jira Cloud issues were read through the REST API (read-only, nothing stored). In every case:
   - `media.attrs` had `type: "file"`, `id` (a Media Services UUID, never the REST attachment id), `collection: ""`,
     `alt`, `width`, `height` and `localId`;
   - `attrs.alt` equalled the filename of one of the issue's attachments;
   - the parents were `mediaSingle` nodes with `layout` `center` or `align-start`, several with `widthType: "pixel"`
     and a pixel `width`.

   The U6 filename mapping is therefore right for images pasted in Jira's editor, and pixel-width media is common
   enough that `unit: "px"` matters. Same-name collisions were not seen in that sample, so the upload-order rule stays
   as U6 wrote it and untested against real data.
9. **Only the drawer loads the Jira renderer.**
   - **Hook.** `render()` takes an `extend(node, api)` hook. `web/src/markup/jira.js` implements it, and the issue
     drawer passes it to `render()` and to the description editor (for islands).
   - **What moved.** Panel, table, expand, task and status rendering moved out of `render.js`. Without the extension
     those kinds keep their content but lose their markup, which no retro page needs.
   - **The allow-list.** The DocNode allow-list (`isAllowedDoc`) moved to `web/src/markup/allow.js`. Only the tests
     use it, so the shipped bundle no longer carries it, and it now covers every new kind.
   - **Brotli sizes** (`buildWeb` into a temporary directory, before → after): jira-drawer 6,309 → 8,382 B (budget
     22,528); poker 28,467 → 28,443; entry 11,838 → 11,853; editor 6,786 → 6,780; shared:parse (render, parse, doc)
     5,853 → 5,714; css (kit) 3,662 unchanged; css:neotro 6,839 unchanged; new route stylesheet css:jira 2,893
     (unbudgeted).
10. **`jira.css` is a poker route stylesheet.** It is listed in `ROUTE_STYLES` with `neotro.css`, scoped to
    `.jira-prose` and `.jira-description-editor`, and wrapped in `:where()` so the drawer's own CSSOM styles still win.
    It also loads for quick sessions with no Jira. The no-Jira check in `submit.spec.js` now counts only API requests.
11. **Read-only task boxes are drawn.** A disabled native checkbox greys out, while Jira shows read-only tasks in full
    colour. The box is `appearance: none` with a CSS-drawn check, and `disabled` stays for safety and accessibility.
12. **The fake Jira shows all of it.**
    - **DEMO-123** (demo preview and screenshots) now has coloured and highlighted text, a subscript, every panel type
      plus a custom panel, a table with a number column, merged cells and cell colours, a two-column layout, an expand
      with a code block, a captioned image, nested tasks, decisions, a status, a date, mentions and an emoji.
    - **RS-12** (the spec fixture) gained a lozenge line, a warning panel and a captioned image after its table.
    - **Fake attachments.** They are now an opaque grey pixel, so an embedded image shows where it sits.
    - **Pinned counts.** `self.test.js` (island ids) and `demo.test.js` (15 islands) were updated, and both now assert
      that the fixture descriptions pass `isAllowedDoc`.

## What still cannot render, and why

- **Remote emoji images and custom emoji.** These are images on Atlassian's CDN, which `img-src` does not allow. A
  standard emoji shows its Unicode text; a custom one shows `:shortName:`.
- **Smart-link previews** (titles, icons and embeds for inline, block and embed cards). They need Atlassian's
  link-resolver service with the viewer's Atlassian session. A card shows the issue key for Jira links, and host + path
  for others.
- **Mention avatars and live names.** They would need Jira's user API per viewer. The pill shows the display text
  stored in the ADF.
- **Media Services previews** of non-image files (PDF thumbnails, video players) and **SVG images**: the attachment
  proxy serves PNG, JPEG, GIF and WebP only (U19). These show as file chips.
- **Extensions and macros** (`extension`, `bodiedExtension`, `inlineExtension`): they are app-defined and still
  flatten to their text.
- **Table `displayMode`, cell `valign`, `breakout` and `border` marks, and annotations** are still ignored. They are
  minor layout hints, and some (annotations) are comment anchors this app does not show.

## Verification

- `./build.sh quick`: passed (Core.Tests 1,776; web-unit and fake-server 170; web build within budgets).
- `./build.sh fake-e2e "poker-jira/*.spec.js"`: 180 passed across chromium, firefox, webkit and mobile-webkit.
- Screenshots of DEMO-123's description (desktop 640 px and iPhone 13, light and dark) were taken with a throwaway
  Playwright script against the demo preview and checked by eye.
- `poker-table/visual.spec.js` fails all 8 shots on the base commit as well, with seat-layout diffs that do not involve
  the description. UX8 changes no pixel those shots cover, so no baseline was regenerated.
