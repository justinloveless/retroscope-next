# UX7 follow-ups: real-stack baselines and the hidden vote pills

UX7 (`docs/decisions/UX7.md`) changed the poker side panels, the toolbar and the results overlay. Its fake specs
and fake baselines were updated and run. The items below need the compose stack, which UX7 could not run.

1. **Regenerate the real-stack visual baselines.** `tests/e2e/real/visual.spec.js-snapshots` (`table`, `strip`,
   `reveal`, desktop and phone, light and dark, darwin and linux) were last refreshed in `0b66acd`. That predates the
   felt stage (`ea876c6`), so they already fail on `main`. UX7 changes the same shots again: the strip no longer ends
   with Next open round and New round, the reveal overlay is centred, and the desktop stage keeps a gutter beside an
   open side panel.
   - **darwin:** `./build.sh e2e "real/visual.spec.js"` with `--update-snapshots`.
   - **linux:** the `run-server` recipe in `docs/decisions/UF16.md`.
   - Review every new PNG by eye before committing.
2. **Retire the hidden vote pills.** `.nt-stacks` (`createResults` in `web/src/poker/reveal.js`) is not shown
   anywhere since UX7. It stays in the overlay's DOM only because these specs read `.nt-stack`, `.nt-stack-pill` or
   `.nt-stack-names`:
   - `tests/e2e/smoke/poker.spec.js`
   - `tests/e2e/real/poker-vote.spec.js`
   - `tests/e2e/real/reconnect.spec.js`
   - `tests/e2e/real/poker-strip.spec.js`

   Move them to what a viewer sees, as the fake specs now do: the piles (`.nt-pile-value`, such as `3 x 5 pts`, and
   each pile's `aria-label`) and the seats' labels (`Gus (guest), voted 3 to 5`). A between vote lies on both of its
   piles, so an assertion on exact ranges belongs on the seats. Then delete `createResults`, its CSS and the
   `.nt-stacks` check in `fake/poker-table/results.spec.js`, and run the real specs.
