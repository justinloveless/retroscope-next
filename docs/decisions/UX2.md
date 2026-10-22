# UX2 decisions: bigger seat cards, gentler side curves, wider between zones

Spec: `docs/followups/UX2.md`. UX2 owns `web/src/poker/**`, `web/styles/neotro.css`, the poker specs and baselines
it lists, and this file.

## Items 1–4: seats and between zones

### Contracts later units rely on

1. **Seat geometry** (`poker/geometry.js`, pure). `seatLayout(stage, count, seat)` keeps its inputs and returns
   `{seats, chip, hidden, depth, sagitta}`; `sagitta` is how far both arcs bow in. Each side is its own circular
   arc. Its chord runs between the top and bottom bands (moved in from the stage edges by half the seat's
   diagonal, so no box leaves the stage), and it bows toward the centre by at most `maxSagitta(width)` =
   min(56 px, 5 % of the stage width). A side's seats are spread evenly by arc length and centred level with the
   stage centre where the arc allows. The whole side is then moved in just far enough that every rotated seat
   clears the 8 px padding, which keeps the arc's shape. Players still alternate left and right, the "+N" chip
   still takes the right arc's last slot, and `angle` is still `atan2` toward the stage centre.
2. **Seat sizes.** `SEAT_SIZES` is `large` (1.5×, 107×143), `full` (1×, 71×95), `compact` (0.75×, 54×72) and
   `avatar`. Each size's box is the card itself, and `inset` says how far the avatar sits in from the card's outer
   end. `planSeats` picks the first size that seats everyone within `depthLimit(width)` = min(220 px, 15 % of the
   stage width).
3. **Seat DOM.** The ring sets `--seat-inset` next to `--seat-w`, `--seat-h`, `--seat-avatar` and `--seat-scale`.
   `data-label` is gone: every ring label hangs just below its avatar.
4. **Between zones.** `gapRect` is unchanged (70 % of the distance between the two cards' centres). While a card is
   dragged, every card to its left moves one slot left and every card to its right one slot right
   (`.nt-dragging .nt-fan-slot`, a 0.15 s slide). That doubles both gaps beside the dragged card, and the zones in
   them. The zones and the drop overlay follow the cards every animation frame while the drag lasts, not only on
   pointer moves, so a zone is never stale after the slide.

### Decisions where the spec was silent or did not fit

1. **The avatar sits on the card.** At 1440×900 the stage between both panels is 800 px wide, so a 560 px issue
   column leaves each side 120 px. A 1× card is 95 px long, and seats near the top and bottom of a side are turned
   toward the centre, so they reach further in. With UX1's layout (avatar beyond the card's outer end) a 6-player
   table could not get 1× cards there. The avatar now sits inside the card's outer end, and the seat's box is the
   card. It covers part of a revealed face's outer end; the seat's label carries the value as a chip.
2. **The sagitta gives way first.** When a side would reach deeper than `depthLimit`, the arc flattens (down to
   a straight column) before the spread shrinks toward the seat's minimum spacing. At 1440×900 with 6 players the
   arcs are therefore straight. With fewer seats, on wider stages and in the 12-player crowd they bow visibly.
3. **Seats centre on the stage's middle height**, not the chord's middle. The bottom band (the hand and its labels)
   is taller than the top band, so the chord's middle sits above the stage centre. Centring there would tilt even
   a lone seat by about 10°. A side's group still stays on the chord.
4. **Labels below the avatar, always.** UX1 put upper-half labels above the avatar. With full-size cards on the
   tighter arcs, those labels landed on the neighbouring seat's card. Hanging every label just below its own
   avatar keeps it on the seat's own card. A label is up to 128 px wide, as in UX1.
5. **The depth limit** is 15 % of the stage width (120 px at 1440×900, so the issue column is exactly 560 px), at
   most 220 px. On a 1920×1080 stage (1280 px) that is 192 px, which fits 1.5× cards for up to 12 players.
6. **What players get, measured on the demo's stages** (`planSeats`, both panels open, up to 17 players): 1440×900
   gets 1× cards up to 7 players, then 0.75×; 1920×1080 gets 1.5× up to 13 players, then 1×; 1280×800 (a 640 px
   stage) gets 0.75× up to 9 players, then avatars. With the Browse Jira panel closed at 1440×900 (1120 px), the 12-player crowd
   gets 1× cards.
7. **The crowded-table spec** (`seats.spec.js`) used 1024×640 to force the "+N" chip. The taller chord now seats 11
   avatar-only players there, so the spec uses 1024×500.
8. **The table oval and its `--nt-felt` token** are removed. The issue panel keeps its own background.

## Results overlay (item 5)

1. **Where it lives.** `poker/results.js` holds the piles (`resultPiles`, pure) and the overlay. `table.js` only
   moves the winner, the round actions and the submit chips (`ctx.slots.results`) between the overlay and the
   dock's bar, so each of those elements exists once in the page.
2. **Two views of the same votes.** The overlay's piles count a between vote on both of its ends, as the scoring
   does (a 3–5 card sits in the 3 pile and the 5 pile), and abstains always get a pile of their own. The compact
   bar keeps UX1's exact-vote pills (`.nt-stack-pill`, "1 x 3–5 pts"); they are in the page in both states and
   hidden while the overlay is open, so the specs and the accessible names that read them still hold.
3. **Memory.** A minimized round is kept in `sessionStorage` (one tab, survives a reload; page memory when storage
   is refused). A live reveal of that round (after a replay) clears it, because it is a new result.
4. **Focus.** On a live reveal the overlay takes focus unless a text field has it. Minimize, Esc and Show results
   always move focus to the other control, because only the viewer's own action gets there (WebKit does not focus
   a clicked button, so "only if focus was inside" lost it there).
5. **Two peers in the fake specs** are two browser contexts of the same viewer on one fake room; the fake serves a
   single viewer. Two real principals are a real-stack concern.

## Baselines and verification (items 1–4, after the overlay landed)

- The fake table's baselines (`tests/e2e/fake/poker-table/visual.spec.js-snapshots`) and the real stack's table and
  reveal baselines (`tests/e2e/real/visual.spec.js-snapshots`) were regenerated on macOS and in the pinned Linux
  Playwright image through `playwright run-server`, as in UF16, with the results overlay in place. The fake ones
  passed `--repeat-each 5` and the real ones `--repeat-each 3`, on both platforms.
- `node --test tests/web-unit/poker/geometry.test.js`: 13 pass. Planting a 2° angle error fails 8 of them, and
  tripling the arc's bow fails 7.
- Screenshots were checked at 1440×900, 1920×1080 and 1280×800 with 3, 6 and 12 players, voting and revealed.
  Capping long seat names at the ring's depth was tried and dropped: on the 640 px stage it cut most names to a
  few letters, so labels keep UX1's 128 px.

## Review fixes

1. **The results overlay keeps clear of the seats' names.** The ring's labels hang up to 128 px in from a side,
   further than the seats themselves. After each layout the table measures how far the drawn labels reach
   (`seats.labelReach()`, which grows when a revealed vote chip joins a name) into `--nt-labels`, and the overlay's
   side inset is the larger of that and `--nt-ring`. When less than 480 px is left between the labels (the 640 px
   stage at 1280×800 with both panels open), the table gets `is-results-narrow`. The piles then deal at 0.75×
   (0.5× from five piles up) with narrower name columns, so the results fit without scrolling. Only the ring
   does this. The phone's seat row has no side labels, and its piles stay full size.
2. **Seats keep their size through a reveal.** The seats plan against the larger of the dock's current height and
   its height before the reveal, so a live reveal (or opening the results) no longer grows the seats, and the
   next round does not shrink them back. A tab that opens straight onto a revealed round has seen no hand yet. It
   plans against the dock it sees, and its seats can shrink once when the next round deals the hand.
3. **No bow at 1440×900 with 6 players and both panels open.** The issue column must stay 560 px wide there,
   so each side may reach 120 px in. Three 1× seats per side already reach 118.7 px as a straight column at
   their closest spacing. The 3 px of slack left would give a bow of about 1 px, which nobody can see. Each 10 px
   of bow costs about 3 px of depth. A visible bow there needs a narrower issue column or smaller cards, so it
   stays straight. The arcs bow visibly on wider stages, with fewer seats, and in the 12-player crowd.
4. **Known flake, outside UX2.** `real/poker-jira.spec.js:96` sometimes times out at 30 s when it is the first
   chromium test of a cold stack. It passes on rerun, and UX2 does not touch the issue drawer. The owner of
   that spec can warm the stack or raise that test's timeout.
5. **The avatar still covers part of a revealed card's outer end** (decision 1 of items 1–4). Moving it out to the
   card's edge on reveal would deepen every seat by about half an avatar and shift the ring at the moment of the
   reveal. The value stays readable on the label's chip and in the overlay. This is left for the owner to judge.
6. **Baselines.** The fake table's two desktop reveal baselines moved, because Bob's seat now keeps its voting place.
   They were regenerated on macOS and in the pinned Linux image (`run-server`, as above). The phone reveal shot was
   already flaky in Linux: the row's small between card sometimes rasterizes its diagonal a pixel apart, 66 pixels in
   all. The spec now waits for the between card's crossfade to end and allows 80 pixels. The fake baselines then
   passed `--repeat-each 8` in Linux and `--repeat-each 5` on macOS. The real-stack baselines pass unchanged on both.
