# Open follow-ups after the final wave

```
U44 done verified= True open= []
   FU: Integrator: after UF16 merges, rerun the short soak on main and confirm the internal_nack warning (SQLSTATE 08/57) is gone. It appeared once in this nightly run, as a warning rather than a gate.
   FU: If a no_snapshot convergence failure ever comes back, read the attempts in the report. A probe stuck at stage 'connect' points to deploy/drills/client.mjs Socket.open, which has no timeout. A bye or 1012 close at welcome/snap points to RoomGateway.SubscribeAsync's Restart() on a busy or unreachable room. Route it to that owner rather than relaxing 
   FU: A CI nightly job that runs the U44 Check still needs to be added by the build.sh/CI owner.
   MINOR: The nightly probe got stuck at stage 'connect' once more in my run: the board probe after the second partition timed out after 6 s, and the second attempt succeeded at 6.8 s. This supports decision 17's guess that the earlier no_snapshot came from de
   MINOR: docs/decisions/U44.md ('third nightly run') says no verifier probe needed a retry. That held for the builder's run (20261022-031248) but not in general: in the verifier's run (20261022-035100), 2 of the 12 convergence checks had a retried probe. One 
   MINOR: The internal_nack warning still appears (1 per run, poker.round.setTicket during the DB restart). This is the known SQLSTATE 08/57 classification issue that UF16 item 1 is fixing. It is not a gate.
UF16 done verified= True open= []
   FU: U12: fix the Leader.CommitFencedAsync comment that says a PostgresException on COMMIT means a certain rollback. That is wrong for SQLSTATE classes 08 and 57.
   FU: U10 and U31 owners: acknowledge UF16's minimal contrast edits in web/styles/tokens.css, kit.css and neotro.css (decision 7).
   FU: Integrator: merge UF16 before U44 as planned. If U44 failed only on the 08/57 classification, rerun the short soak on main after the merge.
   MINOR: docs/decisions/U45.md's UF16 amendment still says the Linux baselines for visual.spec.js are 'still to be recorded (UF16, "Not done")'. The baselines are now committed in feb5833, and UF16.md no longer has a 'Not done' section, so the pointer is stal
   MINOR: Edits to web/styles/tokens.css, kit.css and neotro.css fall outside UF16's listed owned paths. The changes are --green set to #15773a, a new --emerald-strong, and inactive tabs and placeholder chip keys drawn in --foreground at 70%. They are recorded
=== MERGE ===
Merged: UF16 first (b45726d), then U44 (95e7639), both with `git merge --no-ff` and no conflicts. I put UF16 ahead of U44 because the carried follow-up asks for that. Strict unit-number order would put U44 first, but ownership doesn't overlap, so the order makes no difference to the result.

Unmerged: none. Both units were verified.

Final status on main (95e7639):
- **`./build.sh test`**: exit 0.
  - .NET: 3198 total, 3198 passed, 0 failed, 0 skipped (Core, Importer and Integration).
  - Simulation: 2000 of 2000 PR seeds passed.
  - Web unit tests: 349 of 349 passed.
  - Golden vectors were checked against their recorded hashes only, because the original repo wasn't passed in.
- **`./build.sh aot-smoke`**: passed. It ran because the merge changed 4 files under `src/`.
- **Short soak on main** (`--profile short --minutes 4`, run at `nice -n 10`): passed.
  - Every gate was 0, including lost_acked, duplicate, stale_overwrite, wrong_state, timer_missing, timer_expired_twice and rss.
  - The run included kill, db restart (6.4 s) and redeploy outages.
  - The internal_nack warning is gone: there are no warnings, and no `internal` among the final nacks. So UF16's SQLSTATE 08/57 fix holds after the merge.
  - Peak RSS was 74 MiB, ack p99 was 256 ms and throughput was 8.9 acks/s.
  - Report: `artifacts/soak/20261022-045547/report.json`
- No integration fix was needed, so there is no `fix(integration)` commit.
- No soak containers were left running, and the preview on port 5199 was left alone.

Cleanup: I removed both worktrees (`retroscope-next-wt/UF16` and `retroscope-next-wt/U44`) and deleted the `unit/UF16` and `unit/U44` branches.

Logs:
- `<scratch>/w7b-test.log`
- `<scratch>/w7b-aot.log`
- `<scratch>/w7b-soak.log`

Follow-ups (nothing was done on these in this pass):
1. **`deploy/drills/client.mjs` `Socket.open` has no connect/TLS timeout.** The U44 nightly probe hung at the connect stage after a partition and only recovered on retry. The drills client owner should add a timeout. Once that lands, the soak probe's own 6 s per-attempt cut-off can be dropped.
2. **`docs/decisions/U44.md` is too strong about retries.** It says no verifier probe needed a retry, but in the verifier's run 2 of 12 convergence checks retried. Add a note that a non-empty `retriedProbes` is expected after partitions and isn't a regression.
3. **`docs/decisions/U45.md` has a stale pointer.** It still says the Linux baselines are "still to be recorded". Since the file is append-only, add a line saying the 20 `-linux` baselines were recorded in feb5833, pointing to UF16.md's "Linux baselines" section.
4. **UF16 edited CSS files it doesn't own.** It changed `web/styles/tokens.css`, `kit.css` and `neotro.css` (`--green` set to #15773a, a new `--emerald-strong`, and contrast changes to inactive tabs and the placeholder chip key). These are recorded as UF16 decision 7. U10 and U31 should acknowledge the edits; no code change is needed.
5. **The nightly RSS margin is thin.** The latest U44 decisions say the nightly peak was 157 MiB against the 160 MiB gate. Keep an eye on it.
```
