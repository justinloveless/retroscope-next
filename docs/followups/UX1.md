# UX1: poker table redesign, with the issue at the centre

The owner's direction: the issue under discussion is the focus, and the game is played around it.

UX1 owns `web/src/poker/**`, `web/styles/neotro.css`, `tests/web-fake-server/demo/**` (only if a demo shape is missing), `tests/e2e/fake/poker-*/**`, the poker states in `tests/e2e/screenshots/**`, `tests/e2e/real/poker*/**` and the visual baselines it changes, and `docs/decisions/UX1.md`.

## Layout (desktop)

The Jira browse panel (left) and the history panel (right) stay where they are and stay resizable. Everything below happens in the **stage**, the region between them.

1. **Issue details always visible.** The centre of the stage is the current round's issue, rendered with the existing issue-drawer content (key, type glyph, summary, status, assignee, story points, description with islands, links, attachments, comments). The details panel no longer needs a toggle: the "Issue details" header button goes away.
   - The issue view is the stage's scroll container.
   - Its bottom padding always equals the hand's peek height plus the controls bar plus 24 px, measured live with a `ResizeObserver`, so the end of a long description can always be scrolled above the hand.
   - Editing works as it does in the drawer today: description edits keep islands and send a `stale` reload.
   - A round with no Jira key (quick sessions, manual tickets) shows the ticket text or a tidy "No issue linked" state, with "Link a Jira issue" for team sessions that have Jira.
2. **The hand peeks from the bottom.** The fanned hand is fixed to the bottom of the stage and floats over the issue view with a subtle backdrop.
   - At rest only about 35 % of each card is visible.
   - Hover, keyboard focus, drag or touch on the hand raises the whole fan. The selected card stays raised a little more, so you can always see your pick.
   - Lock In, Abstain and Reveal sit in a compact bar attached to the hand, so they float as well.
   - The point-value descriptions show as a tooltip or popover on the hovered or focused card, not as a permanent row of text.
   - Drag-to-play keeps working. The drop target is the issue view: dropping a card anywhere above the hand plays it. The 2-second hold "between" zones become the gaps between adjacent cards in the raised fan.
3. **Seats around a table.** Every other participant sits around the stage as if around a game board, with the issue as the board. Seats always stay inside the stage, never over the Jira or history panels.
   - **Placement.** Seats sit on two arcs of an ellipse inscribed in the stage, one down the left side and one down the right side. They are spread evenly along each arc and fill the two sides alternately, so the table stays balanced.
   - **Keep the top and bottom clear.** No seat goes along the top edge, where the round header and the start of the issue are, or along the bottom edge, where the hand is. Each arc spans at most about 120° of the ellipse. The end seats may spill slightly into the top or bottom corners so they have room to rotate.
   - **Rotation.** Each seat is rotated so its card's top edge points at the centre of the stage. The rotation is `atan2` from the seat's position to the stage centre, so seats near the corners tilt and mid-side seats face straight in.
   - **The seat itself.**
     - The avatar sits on the outer side of the seat, and the card spot sits on the inner, centre-facing side.
     - The name label is drawn upright (counter-rotated) next to the avatar, so it stays readable.
     - A presence dot shows on the avatar.
     - Observers are dimmed and never get a card.
   - **Layout updates.** Positions and angles are recomputed whenever the stage resizes (`ResizeObserver`) or the roster changes. Seats animate to their new spot, with the reduced-motion rule applied.
   - When a player locks in, a face-down card slides into their seat's card spot from the outer edge, travelling along the seat's own axis toward the centre.
   - On reveal, every seated card flips in place, using the existing card-flip timing and reduced-motion rules, and shows its value, or the between pair or abstain.
   - Unlocked selections still show only as a status ("thinking"), so votes stay secret before reveal exactly as today.
   - The current user has no seat. Their locked card shows raised in the hand with a "locked" badge.
   - **Crowded tables.** With more players than fit on the two arcs at minimum spacing, seats shrink down to an avatar-only minimum. Past that, a "+N" overflow chip sits at the end of the fuller arc and opens a popover list.
   - **No overlap with the issue.** The issue view gets left and right padding equal to the seat ring's depth, so the seats sit in reserved side gutters and the text is never covered. Top and bottom keep only the hand rule above.
   - **Golden geometry.** The seat geometry function is pure: it takes the stage size, the seat count and the seat size, and returns positions and angles. It gets unit tests for 1–16 seats and three stage sizes, asserting the following:
     - no seat lies outside the stage
     - no seat lies inside the top or bottom bands, except the allowed corner spill
     - no two seats overlap
     - every angle points within 1° of the centre
4. **Spotlight and round strip** stay above the stage, unchanged. The spotlight follow and preview behaviour must still drive which issue the centre shows.

## Layout (mobile and narrow)

The panels collapse as they do today. Seats become a single horizontally scrolling avatar row pinned under the round header, and locked cards slide into that row. The issue view fills the screen, and the hand peeks from the bottom with the same padding rule. Swipe between rounds keeps working.

## Accessibility and motion

- Each seat has an accessible name, for example "Marcus Chen, locked in" or "Marcus Chen, voted 5" after reveal. Presence and lock changes are announced in a polite live region, at most once per second.
- The hand is keyboard operable: arrows move between cards, Enter selects, L locks, A abstains.
- `prefers-reduced-motion` turns slides and flips into fades.

## Check

All of these must pass:
- `./build.sh test`
- `./build.sh fake-e2e "poker-*/*.spec.js"` on all four browser projects, with specs updated or added for:
  - a long description scrolls fully clear of the hand
  - seats sit only on the left and right arcs, each rotated toward the stage centre (checked from the computed transforms at 3 viewport sizes)
  - a seat card slides in on a peer's lock
  - a seat reveals on reveal
  - no vote value is visible before reveal, asserted in the DOM
  - overflow chip at 12 players
  - mobile seat row
  - drag-to-play and between-hold still work
- `./build.sh e2e "real/poker*.spec.js"`
- `PREVIEW_PORT=5200 ./build.sh screenshots`, with new poker states captured

Visual baselines are regenerated, macOS and Linux.
