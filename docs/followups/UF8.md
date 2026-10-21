# UF8: importer robustness (from wave 3 verification)

UF8 owns: `src/Retroscope.Importer/Map/**`, `tests/Retroscope.Importer.Tests/{Identity,Poker,Retro}/**`, and `docs/decisions/{U39,U40,U41}.md` (append-only). A production import must never abort on one malformed source row; bad rows are skipped, counted and reported.

1. **U39:** `UsersMap.IdentityEmail` parses `identity_data` through `TargetRows.ParseObject`; a malformed value skips that identity and is counted.
2. **U39:** add explicit asserts for each merge primary-key collision rule (memberships keep highest role and earliest join; favorites earliest time; activity latest access; endorsement dedupe; self-endorsements created by a merge are dropped and counted).
3. **U40:** `SessionCodes.Assign` uses `TryAdd` and a deterministic fallback; a room_id equal to another session's id text no longer throws.
4. **U40:** read `average_points` as nullable; a null is reported, never silently 0.
5. **Shared:** add `SyntheticIds.Guest(Guid)` to `Map/Common.cs` and use it in both poker and retro guest mapping, so one browser identity is one principal. Test that a guest who used both poker and retro becomes one principal.
6. **U41:** the `BoardImages` upsert never nulls `created_by` on conflict unless the incoming row is anonymous.
7. **U41:** Markdown links matched by the `TextToken` regex go through the same `IsLinkable` checks (length, control characters) as `<a href>` and keep `mailto:`.
8. **U41:** move `RetroText.Color` (Tailwind → palette) into `Map/Common.cs` and use it for templates too.

**Check:** `dotnet test tests/Retroscope.Importer.Tests` and `./build.sh test` green; items 1, 3, 4, 6, 7 each have a test that fails on the old code.
