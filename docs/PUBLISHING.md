# Before making this repository public

The repository is private. Its history was rewritten on 2026-10-22: one path was removed, wording in documents and code was scrubbed, and commit emails were changed to the GitHub noreply address. Flip it to public only after every item below is done.

1. **Patch or retire the original app.** It is still live, and its access rules have known gaps. The real fix is there, not in this repository.
2. **Rotate the credentials the original exposed.** That means every team's Jira API token and Slack token, and any invite links still in use.
3. **Decide on the Supabase fixture.** `tests/Retroscope.Importer.Tests/Infrastructure/Supabase/` holds the original app's schema dump and its 72 migrations, which include every access policy. The same files are already public in the original repository. Keeping them adds no new disclosure while that repository stays public. If the original goes private or is deleted, replace the fixture with a synthetic schema covering only the tables and columns the importer reads, then remove the old files from every commit with `git filter-repo --invert-paths`.
4. **Neutralise two small importer details.** They hint at how the original stored board passwords: the `RetroMap.cs` report reason "sha256 of an orphaned team board", and the 64-hex board password seeded in `RetroFixture.cs`. Use a neutral reason and a non-hex value, and scrub history in the same `filter-repo` pass as item 3.
5. **Re-run the publish sweep.** Scan every blob and every commit message in the full history for secrets, personal data, local paths and descriptions of live weaknesses before you change the visibility.
