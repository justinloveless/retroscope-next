# Import runbook: Supabase to Retroscope Next

How to move the original app's production data into Retroscope Next, rehearse it, cut over, and roll back. The
design is `docs/DESIGN.md` §h (importer) and §h.6 (cutover). The VPS side reuses `deploy/RUNBOOK.md` sections 8 and 9.
Decisions made while writing this are in `docs/decisions/U46.md`.

The importer is heavy (JIT, AngleSharp, image checks), so it never runs next to the live database on the 1 GB slice.
Everything up to step 6 runs **on the operator's machine** with Docker. The VPS only receives a finished database dump.

## Contents

1. [What you need](#1-what-you-need)
2. [Secrets and environment variables](#2-secrets-and-environment-variables)
3. [The seal key pair](#3-the-seal-key-pair)
4. [The source database](#4-the-source-database)
5. [The local target database](#5-the-local-target-database)
6. [plan, fetch-storage, run, verify](#6-plan-fetch-storage-run-verify)
7. [Reading the report](#7-reading-the-report)
8. [Shipping the result to the VPS](#8-shipping-the-result-to-the-vps)
9. [Cutover order](#9-cutover-order)
10. [Rollback](#10-rollback)
11. [Credential rotation after cutover](#11-credential-rotation-after-cutover)
12. [Cleanup](#12-cleanup)
13. [Rehearsal record](#13-rehearsal-record)

## 1. What you need

- Docker on the operator's machine, with about 2 GB free memory. The commands below use `docker compose` v2.
- The two release images, pulled by digest from GHCR (`deploy/RUNBOOK.md` section 3):
  `ghcr.io/<owner>/retroscope@sha256:<APP_DIGEST>` and `ghcr.io/<owner>/retroscope-import@sha256:<IMPORTER_DIGEST>`.
  They must be the **same release** that runs on the VPS, because the import target is migrated by that release's
  migrator (section 5), and the VPS app's checksum guard refuses a database migrated by other files. To rehearse
  before CI has published images, build them from the release commit:
  `docker build -f deploy/Dockerfile -t retroscope:local .` and
  `docker build -f deploy/Dockerfile.importer -t retroscope-import:local .`.
- Below, `$APP` and `$IMPORTER` stand for those two image references.
- A directory on an encrypted disk for the dump, the report and the seal private key, for example
  `~/retroscope-import` with mode `700`. Everything in it is sensitive: the source data holds emails, password hashes and
  Jira and Slack credentials.

## 2. Secrets and environment variables

The importer reads only environment variables. Keep them in an env file with mode `600` inside the encrypted
directory (`import.env` below), never in shell history and never in the repository.

| Variable | Used by | Value |
|---|---|---|
| `IMPORT_SOURCE_PG` | `plan`, `fetch-storage`, `run` | The source Postgres. For the cutover, the Supabase **session pooler** URI (`postgresql://postgres.<ref>:<password>@aws-0-<region>.pooler.supabase.com:5432/postgres`). TLS defaults to `VerifyFull` and GSS encryption to off unless the value says otherwise. A read-only role is recommended. For a restored dump, a keyword string such as `Host=src;Database=supabase;Username=postgres;Password=…;SSL Mode=Disable`. |
| `DATABASE_OWNER_URL` | every verb | The local import target as `retroscope_owner`: `Host=import-db;Database=retroscope;Username=retroscope_owner;Password=<DB_OWNER_PASSWORD>;GSS Encryption Mode=Disable`. The importer refuses a source and target that are the same database. |
| `APP_KEYS` | `run`, `verify` | **The production value**, exactly as Coolify has it. Jira credentials and link-invite tokens are sealed with it, and `verify` opens them again. A different keyring makes every imported Jira config unreadable in production. |
| `ANON_TAG_KEY` | `run`, `verify` | **The production value.** Anonymous cards and comments are tagged with it, so their authors can still edit them. |
| `IMPORT_SEAL_PUBKEY` | `run` | The RSA-3072 public key from section 3, as PEM (`-----BEGIN PUBLIC KEY-----`). Without it secret archive fields are stripped instead of sealed, which destroys them. |
| `IMPORT_ADMIN_EMAILS` | `run` | Comma-separated emails that become app admins. Source `role='admin'` is ignored (not trusted on import) and every such account is listed in the report. Set at least the operator's own account. |
| `IMPORT_STORAGE_URL` | `fetch-storage` | The Supabase project origin, `https://<ref>.supabase.co`, **without a path**. The importer appends `/storage/v1/object/public/avatars/<user id>.<ext>` itself and refuses a value with a path, such as the bucket URL the dashboard shows. |
| `JIRA_HOST_ALLOWLIST` | `run` | Optional, the same value as production. Jira sites outside Atlassian Cloud need it. |

`DB_OWNER_PASSWORD` for the local target is any throwaway value; it never reaches the VPS (section 8 uses
`--no-owner --no-acl`).

## 3. The seal key pair

Archived secrets (auth tokens, phone numbers, the bcrypt hashes of merged duplicate accounts, `identity_data`, Jira and
Slack tokens, invite tokens and codes, sensitive `app_config` values) are sealed: each run makes a random AES-256-GCM
key, wraps it with RSA-OAEP-SHA256 under `IMPORT_SEAL_PUBKEY`, and stores the wrapped key in
`archive.import_runs.report -> 'facts' -> 'seal'`. The server can never open them. The operator can, offline, with the
private key and `Retroscope.Core.Security.Sealer.TryOpen` (AAD = archive row `source_table`, `source_pk`, field name).

Generate the pair **once**, on the operator's machine, and keep the private key offline (a password manager or an
encrypted USB key). It never goes to the VPS, to Coolify or to CI.

```sh
umask 077
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out ~/retroscope-import/seal.key
openssl pkey -in ~/retroscope-import/seal.key -pubout -out ~/retroscope-import/seal.pub
```

Pass the public key with `-e IMPORT_SEAL_PUBKEY="$(cat ~/retroscope-import/seal.pub)"`. Move `seal.key` off the
machine once the run has been reviewed.

## 4. The source database

### 4a. For the cutover: live Supabase

The cutover imports from the live project through the session pooler (`IMPORT_SOURCE_PG` above), with the old app in
maintenance so nothing changes underneath. The importer reads everything in one `REPEATABLE READ READ ONLY`
transaction, reads `storage.objects` and `cron.job` from the live project, and only counts the tables it excludes.

### 4b. For rehearsals: a restored dump

The final backup (cutover step 2) and every rehearsal dump use this command (the rehearsal dump of 2026-10-21 was made
this way: `pg_dump` 17.11 against Supabase Postgres 15.8, custom format):

```sh
pg_dump --format=custom --no-owner --no-privileges --schema=public --schema=auth \
  --file ~/retroscope-import/retroscope-supabase.dump "$SUPABASE_SESSION_POOLER_URI"
```

A dump of `public` and `auth` lacks what Supabase creates outside them: the platform roles, the `extensions` schema,
`storage` and `cron`. Restore it into a throwaway `postgres:15` with those stubs first. A `pg_dump` 17 archive needs a
17 client, so `pg_restore` runs from `postgres:17`. The dump is mounted read-only and the database lives on tmpfs.

```sh
docker network create import-src
docker run -d --name import-src --network import-src -e POSTGRES_PASSWORD=<throwaway> -e POSTGRES_DB=supabase \
  --tmpfs /var/lib/postgresql/data \
  postgres:15@sha256:c99dd5066e988cd61a72ffe962da08e277eecaba000bbdda0bdaf9f63b56c174

# Platform stubs: roles, extensions, storage and cron (the storage and cron tables are the ones in
# tests/Retroscope.Importer.Tests/Infrastructure/Supabase/00-platform-stubs.sql, from "create schema storage;" on).
docker exec -i import-src psql -v ON_ERROR_STOP=1 -U postgres -d supabase <<'SQL'
create role anon nologin; create role authenticated nologin; create role service_role nologin;
create role supabase_admin nologin; create role supabase_auth_admin nologin; create role supabase_storage_admin nologin;
create role authenticator nologin; create role dashboard_user nologin; create role pgbouncer nologin;
create schema extensions;
create extension pgcrypto with schema extensions;
create extension "uuid-ossp" with schema extensions;
SQL
sed -n '/^create schema storage;/,$p' tests/Retroscope.Importer.Tests/Infrastructure/Supabase/00-platform-stubs.sql \
  | docker exec -i import-src psql -v ON_ERROR_STOP=1 -U postgres -d supabase

docker run --rm --network import-src -e PGPASSWORD=<throwaway> \
  -v ~/retroscope-import/retroscope-supabase.dump:/dump/source.dump:ro \
  postgres:17@sha256:a63c1ea3f6a3a5a1621f97d2aba3b30faecfab4224c1b0000cc85ced57bb1939 \
  pg_restore --no-owner --no-privileges -h import-src -U postgres -d supabase /dump/source.dump
```

`pg_restore` reports two harmless errors and exits 1: `schema "public" already exists` and `unrecognized configuration
parameter "transaction_timeout"` (a PostgreSQL 17 setting the 15 server does not have). Any other error is real.

The dump has no `storage.objects`, so `fetch-storage` would find no avatar objects. To rehearse avatars, list each
profile's own avatar object from its `avatar_url` (rehearsal only; the live project has the real listing):

```sh
docker exec import-src psql -U postgres -d supabase -c "
  insert into storage.buckets (id, name, public) values ('avatars', 'avatars', true);
  insert into storage.objects (bucket_id, name, metadata)
  select 'avatars', substring(avatar_url from '/avatars/([^?#]+)'), '{}'::jsonb
  from public.profiles where avatar_url like '%/storage/v1/object/public/avatars/%';"
```

`cron.job` stays empty in a rehearsal; the live project's jobs are recorded in the report at cutover.

## 5. The local target database

Start the import target from the repository (`deploy/compose.build.yml`, profile `import-local`; Postgres 18 on
`127.0.0.1:55432`):

```sh
DB_OWNER_PASSWORD=<throwaway> docker compose -p rs-import -f deploy/compose.build.yml --profile import-local \
  up -d --wait import-db
docker network connect rs-import_default import-src   # rehearsal only: lets the importer reach the restored dump
```

The importer does not create the schema. Migrate the empty database with **the release's own app image**: `spot-load`
takes the leader lease, which runs the migrator, and then finds no rooms.

```sh
docker run --rm --network rs-import_default \
  -e PUBLIC_URL=https://<production host> \
  -e "DATABASE_URL=Host=import-db;Database=retroscope;Username=retroscope_app;Password=<any>;GSS Encryption Mode=Disable" \
  -e "DATABASE_OWNER_URL=Host=import-db;Database=retroscope;Username=retroscope_owner;Password=<DB_OWNER_PASSWORD>;GSS Encryption Mode=Disable" \
  -e APP_KEYS -e ANON_TAG_KEY -e SMTP_HOST=localhost -e SMTP_FROM=noreply@example.invalid -e TRUSTED_PROXY_CIDRS=127.0.0.1/32 \
  $APP spot-load --sample 1
# Applied 3 migration(s) … spot-load: poker 0, board 0, team 0 loaded; 0 failed
```

## 6. plan, fetch-storage, run, verify

Every verb runs the importer image on the target's network with the env file. Keep stdout in the encrypted
directory: `plan` prints the whole report, including `cron.job` commands and RLS policies, and the `fetch-storage`
skip list names user ids.

```sh
I="docker run --rm -i --network rs-import_default --env-file $HOME/retroscope-import/import.env"

$I $IMPORTER plan > ~/retroscope-import/plan.json        # exit 0: no unknown tables, columns or HTML tags
$I $IMPORTER fetch-storage                               # avatars into archive.storage_cache (resumable)
$I -e IMPORT_SEAL_PUBKEY="$(cat ~/retroscope-import/seal.pub)" $IMPORTER run > ~/retroscope-import/run.txt
$I $IMPORTER verify                                      # verify: 11 checks, 0 failures
```

- **`plan` exits 1** when the source differs from `src/Retroscope.Importer/Map/expected-columns.json` (a missing,
  unknown or retyped column, an unlisted `auth` table) or holds an HTML tag the converter does not know. Nothing is
  written. Fix the cause in the repository (classify the column, add an `exclude` entry for a new GoTrue table), and
  release again. Two operator switches exist for one-off cases: `allowArchiveOnly` entries in `expected-columns.json`
  for an unknown `public` table or column (archived whole, never imported), and `--html-textonly <file>` for extra tags
  whose text should be kept (same shape as `src/Retroscope.Importer/Source/html-textonly.json`). If
  `public.team_action_item_comments` ever appears (production never ran that migration), allow-list it.
- **`fetch-storage`** downloads, with concurrency 4, each kept user's own `avatars/<user id>.<ext>` object and the
  `lhN.googleusercontent.com` avatar URLs that profiles point at. It accepts only PNG, JPEG and WebP of at most 256 KiB.
  Re-running it skips what is already cached.
- **`run`** fences the target (bumps `leader_epoch`, so an app still attached to it fails its next write), then
  truncates and reloads every app table in **one transaction**, archives dropped tables and columns, and runs every
  verification check before `COMMIT`. A failed check rolls everything back. Re-running before cutover is safe and
  deterministic. It prints a per-table summary. Since UX18 every room belongs to a team: source poker sessions and
  retro boards with no team (the original's quick rooms) are skipped and counted, boards get no visibility or
  password, and favorites and board or session recency are not imported (`docs/DESIGN.md` §h.2).
- **`verify`** repeats the checks read-only. Run it between `run` and `cutover`. After cutover, users change passwords,
  so the `password-hashes` check reports differences by design.

## 7. Reading the report

`run` stores the full report as JSON in `archive.import_runs.report`. It contains emails and ids. Read it locally:

```sh
docker exec rs-import-import-db-1 psql -U retroscope_owner -d retroscope -Atc \
  "select report from archive.import_runs where status = 'succeeded' order by started_at desc limit 1" \
  > ~/retroscope-import/report.json
```

- `tables`: per source table, `source = imported + archived + sum(skipped)`. The `counts` check already enforces it.
- `counters` and `findings`: the soft checks of §h.5. Review before cutover:
  `admins` and `ignoredAdminRoles` (who is admin), `jiraRotateRequired` (teams whose Jira token must be rotated),
  `jiraConfigsRejected` (teams that lose their Jira config; the owner re-enters it), the skipped team-less boards and
  sessions (UX18; the first import had `rotatedBoardPasswords` instead), `mergedAccounts`,
  `nulledPasswordHashes` (users who must reset), `skippedPhoneOnly`, `recodedBoards` (old links with these codes stop
  working; the board is reachable by id),
  `pokerRenumberedRounds`, `pokerWinnerMismatches`, `skippedAvatars`, `emailsInFullName`.
- `facts.autoconfirmProbe`: accounts whose email was never confirmed by mail. Every imported account starts with an
  unverified email, except Google sign-ins whose Google email matches.

## 8. Shipping the result to the VPS

Ship the **whole** import database, schema included, and restore it into an empty database, exactly like a backup
restore (`deploy/RUNBOOK.md` section 8). The app's checksum guard then confirms the schema came from the same release.

```sh
docker exec rs-import-import-db-1 pg_dump -Fc -U retroscope_owner -d retroscope > ~/retroscope-import/rs-import.dump
scp ~/retroscope-import/rs-import.dump <vps>:/root/rs-import.dump      # mode 600 on the VPS, deleted after step 9
```

On the VPS, with **the app stopped in Coolify** (the database keeps running):

```sh
docker exec <db> psql -U retroscope_owner -d postgres \
  -c "drop database retroscope with (force)" -c "create database retroscope"
docker exec -i <db> pg_restore --exit-on-error --no-owner --no-acl -U retroscope_owner -d retroscope < /root/rs-import.dump
docker run --rm --network container:<db> -e DATABASE_URL=… -e DATABASE_OWNER_URL=… -e PUBLIC_URL=… \
  -e APP_KEYS=… -e ANON_TAG_KEY=… -e SMTP_HOST=… -e SMTP_FROM=… -e TRUSTED_PROXY_CIDRS=… \
  ghcr.io/<owner>/retroscope@sha256:<APP_DIGEST> spot-load --sample 20
# spot-load: poker N, board N, team N loaded; 0 failed      (exit 0; --sample 10000 loads every room)
```

`spot-load` refuses to run while an app holds the leader lease. Then start the app in Coolify and wait for healthy.

## 9. Cutover order

1. Announce the window. Put the old app into maintenance (no writes from here on).
2. Take the final Supabase backup (section 4b command) and keep it with the seal key.
3. Off-box, against live Supabase: `plan`, `fetch-storage`, `run`, `verify` (sections 5 and 6).
4. Review the report (section 7): admins, Jira configs requiring rotation or rejected, skipped team-less rooms,
   merged emails, nulled hashes, recoded boards.
5. Stop the app on the VPS; restore the import dump; `spot-load --sample 20` (section 8).
6. Start the app; check `/healthz` and sign in as an admin.
7. `cutover` against the VPS database, from the importer image on the VPS network (app running):
   `docker run --rm --network container:<db> -e DATABASE_OWNER_URL=… $IMPORTER cutover`.
   From now on `run` refuses unless given `--force-reimport --i-have-a-dump=<path>` and the database name at a prompt.
8. Switch the Coolify domain or DNS to the new app. Old links keep working for members: `/retro/:code`,
   `/poker/:code` and `/teams/:teamId/poker/:code` open for a signed-in member of the room's team (UX18), and
   `/invite/:token` for everyone.
9. Rotate credentials (section 11) and clean up (section 12).

## 10. Rollback

- **Before step 8 (DNS):** nothing has changed on the Supabase side; the importer only reads it. Take the old app out
  of maintenance and stop the new app. Retry later from step 3: `run` re-imports from scratch.
- **After step 8, before anyone relies on the new app:** point the domain back at the old app and lift maintenance.
  Writes made in the new app in between are lost to the old one; say so in the announcement.
- **Restoring the VPS database:** the backup sidecar keeps daily dumps (`deploy/RUNBOOK.md` section 8). Restore one
  the same way as section 8 above, with the app stopped.
- Do not delete the Supabase project or rotate its database password until the new app has run for an agreed period
  (two weeks suggested). Rotated Jira tokens (section 11) also break the old app's Jira integration, so a team that
  rotates before a rollback re-enters its new token in the old app.

## 11. Credential rotation after cutover

Treat every credential imported from the original as compromised and rotate it:

- **Jira API tokens:** every team listed in `jiraRotateRequired` has `status = 'rotate_required'`. Each team owner
  revokes the old token at id.atlassian.com, creates a new one and enters it in the team's Jira settings. Teams in
  `jiraConfigsRejected` start without Jira; their owner enters it fresh.
- **Slack bot tokens and webhook URLs** (archived sealed, not used by the new app): revoke them in the Slack app's
  settings.
- **Link invites** are treated as compromised: they import with role `member` and expire at most 7 days after the
  import. Team admins re-issue what they still need.
- **Board passwords** no longer exist (UX18): every board is open to its team's members only, and the importer
  honours no source password. (The first import rotated the passwords of private quick boards.)
- **Supabase:** rotate the database password and the JWT secret (which invalidates the `anon` and `service_role`
  keys), then pause or delete the project once the rollback window is over.
- Remove `IMPORT_SOURCE_PG`, `IMPORT_SEAL_PUBKEY`, `IMPORT_ADMIN_EMAILS` and `IMPORT_STORAGE_URL` from Coolify and from
  `import.env`. `APP_KEYS` and `ANON_TAG_KEY` stay as they are: the imported boxes and tags depend on them.

## 12. Cleanup

```sh
docker compose -p rs-import -f deploy/compose.build.yml --profile import-local down -v
docker rm -f import-src; docker network rm import-src
rm ~/retroscope-import/rs-import.dump ~/retroscope-import/plan.json ~/retroscope-import/run.txt
ssh <vps> rm /root/rs-import.dump
```

Keep the final Supabase dump, `report.json` and `seal.key` together, offline, for as long as sealed values might need
opening, then destroy them together.

## 13. Rehearsal record

2026-10-21, against a `pg_dump` of production (section 4b), all on a development machine with throwaway keys:

- `plan`: 0 unknown tables, columns or HTML tags after three production findings were folded into the repository
  (`docs/decisions/U46.md`): eleven `auth.users` columns are `varchar` in production, sixteen further GoTrue tables are
  excluded, and `public.team_action_item_comments` does not exist in production. HTML seen: `p`, `br`, `a`, `em`.
- `fetch-storage`: 15 candidates (9 own avatar objects, 6 Google avatars), 15 fetched, 0 skipped.
- `run` twice: identical summaries; `verify`: 11 checks, 0 failures.
- Full `pg_dump -Fc` of the target restored with `pg_restore --exit-on-error --no-owner --no-acl` into the
  `deploy/docker-compose.yml` stack; `spot-load --sample 20` and `--sample 10000` exit 0 (37 poker sessions,
  149 boards, 13 teams, 0 failed); the app started healthy; `cutover` succeeded and a later `run` refused.
