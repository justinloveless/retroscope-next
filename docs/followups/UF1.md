# UF1: Wave 0–1 follow-ups

UF1 owns exactly the files named below, and only for these changes. Each item came from a verifier or integrator report on a merged unit.

1. **U1** `tests/Retroscope.Integration.Tests/Schema/SpotlightConstraintTests.cs`: the deferred-FK tests must run the DELETE, assert it succeeds, then assert that `CommitAsync` is the call that throws 23503. A non-deferrable FK must fail the test.
2. **U2** `docs/decisions/U2.md`: renumber the conventions list (it runs 1, 2, 5, 3, 4) and correct the stale Check test count.
3. **U3** `src/Retroscope.Core/Security/Sealer.cs`: `TryOpen` returns false on a null `sealedValue` instead of throwing. Add a test.
4. **U3** `src/Retroscope.Core/Security/AnonTagger.cs` (and its tests): take `BoardId`/`PrincipalId` instead of raw `Guid`s now that U2's typed ids exist. Output bytes must not change; pin that with a fixed vector.
5. **U3** `docs/decisions/U3.md`: note that `Keyring.Mac` always uses the active key, so audit HMACs from before and after a rotation do not match (audit writers must not join across rotations).
6. **U4** `tests/Retroscope.Core.Tests/Markup/**`: add hostile-input timing cases at Jira length (32k chars: nested emphasis, unclosed links, long mention runs) asserting parse completes under 200 ms.
7. **U10** `tests/web-unit/core/**` banned-sink test: scan every file under `web/src/**`, not only modules reachable from `entry.js`.
8. **U10** `web/src/core/router.js`: a superseded navigation must not call `onError` or `location.assign` with the old URL. Add a test.
9. **U47** `tests/golden/make-golden.mjs`: `--orig` without a path fails with a usage error.

**Check:** `./build.sh test` green, with the new tests listed above present and passing.

## Revision 2 (after wave 2)

UF1 resumes on its existing branch `unit/UF1`. Ownership is widened to two more files, because their original owners (U4, U5) are finished:

10. `src/Retroscope.Core/Markup/InlineParser.cs` and `web/src/markup/parse.js`: make link/label scanning linear. Precompute, in one right-to-left pass per paragraph, the next unescaped `]` and `\n` for every index so `ScanLink`/`ScanLabel` are O(1) per `[`. Keep the two parsers' outputs identical over the shared corpus. Remove every `Skip = QuadraticLinkScan` row in `MarkupJiraLengthTimingTests.cs`; all rows must pass under 200 ms.
11. Add a node test (`tests/web-unit/golden-cli.test.js`) that runs `tests/golden/make-golden.mjs --orig` and `--orig --verify` and asserts exit code 2 plus the usage text.

**Check:** `./build.sh test` green with zero skipped timing rows.
