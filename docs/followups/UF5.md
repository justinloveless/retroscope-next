# UF5: web fixes from wave 2 verification

UF5 owns these paths for these changes only: `web/src/markup/editor.js`, `tests/web-fake-server/**`, `tests/e2e/fake/_harness/**`, `tests/e2e/fake/editor/**`, `.gitignore`, `docs/decisions/{U34a,U48}.md` (append-only).

1. **Editor (U34a):** `insertImages` awaits `uploadImage`; a rejection removes the placeholder and calls an `onError` option (surfaced by the caller as a toast). No unhandled rejections. Add a spec.
2. **Editor:** delete the duplicate `tests/e2e/fake/editor/playwright.config.js`; correct decision 9 to mention the mention-listbox `left`/`top` CSSOM positioning.
3. **Fake server (U48):** allow a scripted `cid_reuse` nack for a dup whose payload hash differs (DESIGN §c.6), so U28 can test it.
4. **Fake server:** the "teeth" specs assert the specific CSP or protocol violation instead of relying on `test.fail()`.
5. **Fake server:** WebKit reports close code 1012 as 1005; document that in the harness README for U28.
6. `.gitignore`: add `test-results/` and `playwright-report/`.

**Check:** `./build.sh test` and `./build.sh fake-e2e "_harness/*.spec.js"` and `./build.sh fake-e2e "editor/*.spec.js"` green.
