# UX7 decisions: drawer handles, round tools in the toolbar, centred results

The owner asked for four changes to the poker session screen. UX7 does them as three units. It owns
`web/src/ui/side-panel.js`, the poker files below, `web/styles/neotro.css`, the specs and baselines it lists, and this
file.

## Unit 1: drawer handles for both side panels

The owner's request: "Let's make it so that the browse Jira panel and history panel both have the same little drawer
handles that the old app did to control the width and to open/close the panels. They shouldn't be separate buttons
like the 'Browse Jira' button in the header."

### Contracts later units rely on

1. **`createSidePanel({panel, edge, name, openIcon, width, open, onToggle, onCommit})`** (`ui/side-panel.js`) gives a
   side panel the original's three handles:
   - a resize strip on the panel's inner edge with a grip chip (`createResizable`, unchanged: `role=separator`,
     `aria-valuenow/min/max`, arrow keys, 240–480 px, pointer capture), labelled "Resize {name}";
   - a 24×40 px chevron tab, "Collapse {name}", shown while the panel is open;
   - a 32×96 px tab with `openIcon` on the screen edge, "Open {name}", shown while it is closed.

   Both tabs carry `aria-expanded` and `aria-controls` (the panel's id), so the panel needs an id. A tab only calls
   `onToggle(open)`; the owner calls `setOpen(open, {focus})` when the panel is ready. With `focus`, the focus moves
   to the other tab, as the History panel's collapse did.
2. **Names and icons.** The Jira panel is "Jira panel" with `search`; the Round panel is "round panel" with
   `list-ordered`, the icon of the phone's Round history button. The panels are `#nt-panel-left` and
   `#nt-panel-right`.
3. **Persistence is unchanged:** `localStorage['rs.panels']` holds `{left: {open, width}, right: {open, width, tab}}`.
4. **Phones** have no side panels or tabs (below 768 px). The browse sheet opens from a "Browse Jira" button in the
   bottom bar, after the separator and before Round history. It is shown only for teams with Jira. Features add
   such buttons through the new `ctx.slots.bottom`, which `mobile.js` places in the bar.

### Decisions where the request was silent or did not fit

1. **The panels keep reflowing the stage.** In the original both panels float over the playing field. Here they stay
   in the flex row, as before. Floating them would change every seat layout and the visual baselines, and the
   request is about the handles. Only the open tabs float, over the stage's outer edge (`.nt-body` is their
   containing block), so a closed panel gives the stage its full width.
2. **The collapse tab straddles the panel's edge, and the grip chip sits just above it.** In the original the
   collapse tab covers the grip chip, because both are centred on the same edge. The tab here is centred on the edge,
   12 px over the panel and 12 px over the stage. That keeps it off the Jira rows' add buttons and the history
   entries. On its own it still covered the outermost seat's avatar at 1280 and 1440 px, so on desktop the stage keeps
   a 12 px gutter beside each open panel (padding on `.nt-center`), and the tab's overhang sits in that gutter. The
   seats are measured inside the gutter, so they move in with it. The chip moves 2 rem up so both stay visible.
   Dragging works anywhere on the strip outside the tab.
3. **The panels show their tabs over the stage**, so they no longer clip their overflow (`overflow: visible`). The
   browse list keeps its own clipping, and the history body scrolls as before. Panels sit at `z-index: 7`, above the
   stage's dock (6).
4. **The Jira panel opens before its chunk loads.** On a click, or at load when it was left open, the panel takes its
   width at once and the browse list fills it when `jira-browse.js` arrives. Before, the slot stayed hidden until
   then, so the stage moved twice. If the chunk fails to load, the panel closes again with an error toast, and the
   next open retries the import.
5. **Colours** come from the kit's tokens (`--card` at 90 %, `--border`, `--accent` on hover, `--ring` for focus), so
   both themes follow. The original's tabs had no focus style. These have the kit's focus ring.

### What it overrides

- **U33 decision 6:** the toolbar's "Browse Jira" toggle (`.jira-toggle`) is gone. The left panel's tabs open and
  close it on desktop, and the bottom bar's button opens the sheet on phones.
- **U32 contract 2 / UF14 item 4:** the Round panel's "Collapse panel" head button and its vertical "History" side tab
  are replaced by the drawer handles. The Jira panel's resizer moves into the same primitive. Widths, limits and
  storage keys are unchanged.
- **DESIGN §g.8** says the panels are "collapsible with vertical tab buttons". The edge tabs are still tab buttons, now
  with icons instead of a vertical label, so DESIGN is unchanged.

## Unit 2: the round tools in the toolbar

The owner's request: "The 'play' and 'plus' buttons in the round selector should be moved to the top where the left
and right buttons are instead of being way off to the right in the round selector."

1. **"Next open round" (▶) and "New round" (+) sit in the toolbar's Rounds nav**, after Previous round, the round's
   name and Next round. The strip feature still owns them (labels, icons, handlers). It
   appends them to the new `ctx.slots.rounds` (the `nav.nt-toolbar-center`) and removes them on destroy.
   `.nt-strip-tools` is gone, and the strip holds only the chips.
2. **Phones give the Rounds nav a toolbar row of its own.** With four buttons beside it, the round's name was cut to
   "Round 1 · A…" at 390 px. Back and the account controls now share the first row, and the nav is centred on the
   second row. The toolbar keeps its two rows.
3. **This overrides U32 decision 5**, which put both buttons at the end of the strip because U31 owned the toolbar.

## Unit 3: round results

The owner's request: "Let's make the round results show up centered vertically in the stage instead of being near the
top. And when the round results are minimized, let's not show the Winning Points or the point count buckets, and
instead just put the Show Results button inline with the Submit points button. We also probably don't need the
replay button when minimized either."

1. **Centred above the dock.** The overlay's top is the middle of the stage's free height,
   `(100% - --nt-dock-h) / 2`, and it is moved up by half its own height. Its `max-height` already keeps it 0.75 rem
   clear of the stage's top and the dock. Horizontal centring, the side insets, `is-results-narrow` and the
   overflow are unchanged. The entry animation starts 0.75 rem higher than the overlay's place, as before. Reduced
   motion still only fades.
2. **Minimized, the dock keeps only what moves the round on.** The winner, the exact-vote pills and Replay stay in
   the hidden overlay. The bar holds End/Continue or Next Round, if the round is still active.
3. **Show results sits beside Submit when there is one.** The Jira feature marks the row that holds its Submit button
   (`[data-results-inline]`, `.jira-submit-send`), and the table appends Show results there while the results are
   minimized. Without Jira, or while the submit section is hidden, Show results leads the bar, before
   End/Continue/Next Round. Features update after the table, and a team's Jira settings can arrive later, so the
   table follows the row with a `MutationObserver` on its results slot (`hidden` and child changes). When nothing is
   left to show, the dock's bar hides, for example on a minimized round that is over and has no Submit.
4. **Phones** put the submit chips on their own row, with Submit and Show results side by side below them. Before,
   the chips were squeezed into a narrow column.
5. **Focus is unchanged:** Minimize and Esc focus Show results wherever it sits, and Show results focuses the overlay.
6. **The pills stay in the page for now.** `.nt-stacks` is no longer shown in either state. The fake specs now read
   the revealed votes from what a viewer sees: the piles (`.nt-pile`) and the seats' labels. The list stays inside
   the overlay's hidden part only because the real-stack and smoke specs still read exact votes from
   `.nt-stack-pill`, and those specs could not be run here. Moving them and deleting the list is tracked in
   `docs/followups/UX7.md`.

### What it overrides

- **UX2 decision 2** (results overlay, item 5): the compact bar no longer shows the pills or the winner.
- **UX1's results bar** (winner | pills | round actions): the minimized bar is only the round actions and, without
  Jira, Show results.
- **UX2's overlay position** (top-anchored at 0.75 rem): it is now centred.

## Baselines and verification

- The fake table baselines (`tests/e2e/fake/poker-table/visual.spec.js-snapshots`) were regenerated after each unit on
  macOS and in the pinned Linux image (the `docker run` command in UF14 item 3). The final set passed
  `--repeat-each 3` on both platforms. Both the desktop and the phone baselines on `main` were already stale: they
  were last refreshed before the felt stage (`ea876c6`) and still showed a plain stage. So the first regeneration
  (`0e25ff3`, "for the drawer handles") also contains the UX6 felt, rail and seat changes, not only the handles. A
  later regeneration adds the stage gutter beside open panels.
- `tests/e2e/real/visual.spec.js-snapshots` (`strip`, `reveal`, `table`) were **not** regenerated, because that needs
  the compose stack. The strip no longer ends with the two buttons, and the reveal is centred, so those baselines
  need `./build.sh e2e "real/visual.spec.js"` with `--update-snapshots` (darwin) and the UF16 `run-server` recipe
  (linux). They were already red on `main`, though: they were last refreshed (`0b66acd`) before the felt stage, so
  they show a plain stage. UX7 does not make them fail, but they must be regenerated once for both changes before the
  real suite can pass. This is tracked in `docs/followups/UX7.md`.
- `real/_jira.js` and `real/poker-jira.spec.js` now open the browse panel through the new handles and the bottom bar,
  like the fake helpers. They were updated by reading the code, not run.
- `fake/poker-table/felt.spec.js:68` (all engines), `:94` and `:105` (WebKit) and `seats.spec.js:173` (all engines)
  fail on `main` (`aff82b4`) as well, without UX7's changes. UX7 does not touch the felt print or the hand's resting
  peek.
