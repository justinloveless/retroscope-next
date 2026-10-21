create schema if not exists archive;

create table schema_migrations (
  version    int primary key,
  name       text not null,
  sha256     bytea not null,                 -- migrator refuses to start if an applied file changed
  applied_at timestamptz not null default now()
);

create table system_state (                  -- single row
  id              boolean primary key default true check (id),
  leader_epoch    bigint not null default 0, -- bumped by every instance that becomes leader; fences all actor writes
  leader_instance uuid,
  leader_since    timestamptz,
  cutover_at      timestamptz                -- set by `retroscope-import cutover`; importer refuses afterwards
);
insert into system_state default values;

-- Epoch fence (R1, F3). First statement of every actor transaction: `select rs_fence($epoch)`.
-- Takes a SHARED transaction-level advisory lock (shared memory, no tuple lock, no WAL, no MultiXact)
-- and raises inside SQL on mismatch, so the rest of the batch aborts and nothing commits.
-- A new leader bumps the epoch under the EXCLUSIVE xact lock on the same key, which waits for
-- every in-flight fenced transaction of the old leader to finish.
create function rs_fence(expected bigint) returns void language plpgsql as $$
begin
  perform pg_advisory_xact_lock_shared(1381123153);         -- K_FENCE
  if (select leader_epoch from system_state) <> expected then
    raise exception 'fencing_lost' using errcode = 'RS001';
  end if;
end $$;
revoke execute on function rs_fence(bigint) from public;   -- the migrator grants it to retroscope_app only

------------------------------------------------------------------------------- identity
create table principals (
  id           uuid primary key,
  kind         text not null check (kind in ('user','guest','external')),   -- external = imported Slack voter
  display_name text check (display_name is null or char_length(display_name) between 1 and 60),
  created_at   timestamptz not null default now(),
  check (kind = 'user' or display_name is not null)                         -- users derive names from users row
);

create table blobs (                          -- avatars and pasted board images; bytes in Postgres (no bind mounts)
  id           uuid primary key,
  purpose      text not null check (purpose in ('avatar','board_image')),
  board_id     uuid,                          -- FK added after retro_boards
  content_type text not null check (content_type in ('image/png','image/jpeg','image/gif','image/webp')),
  byte_size    int  not null check (byte_size between 1 and 2097152),
  sha256       bytea not null check (octet_length(sha256) = 32),
  bytes        bytea not null,
  created_by   uuid references principals(id) on delete set null,   -- NULL for images in anonymous cards/comments (S6)
  attached_at  timestamptz,                    -- set when a committed card/comment references it; GC deletes
                                               -- unattached board images after 24 h (S7)
  created_at   timestamptz not null default now(),
  check ((purpose = 'board_image') = (board_id is not null)),
  check (purpose <> 'avatar' or byte_size <= 262144)
);
alter table blobs alter column bytes set storage external;      -- already-compressed media: skip TOAST compression
create index blobs_board_ix on blobs (board_id) where board_id is not null;
create index blobs_unattached_ix on blobs (created_at) where purpose = 'board_image' and attached_at is null;

create table users (
  id                uuid primary key references principals(id) on delete cascade,
  email             text not null check (email = lower(email) and char_length(email) between 3 and 254
                                         and position('@' in email) > 1),
  email_verified_at timestamptz,               -- set only on proven ownership; imported password accounts start NULL (S1)
  password_hash     text check (password_hash ~ '^\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}$'),  -- bcrypt only
  full_name         text not null default '' check (char_length(full_name) <= 100),
  nickname          text check (nickname is null or char_length(nickname) between 1 and 40),
  avatar_blob_id    uuid references blobs(id) on delete set null,
  theme             text not null default 'system' check (theme in ('light','dark','system')),
  is_admin          boolean not null default false,
  disabled_at       timestamptz,
  mentions_seen_at  timestamptz not null default now(),
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);
create unique index users_email_uq on users (email);
create index users_name_prefix_ix on users (lower(full_name) text_pattern_ops);   -- admin search

create table external_identities (
  provider   text not null check (provider in ('google','entra')),
  subject    text not null check (char_length(subject) <= 200),   -- google: sub; entra: "<tid>:<oid>"
  user_id    uuid not null references users(id) on delete cascade,
  linked_at  timestamptz not null default now(),
  primary key (provider, subject)
);
create index external_identities_user_ix on external_identities (user_id);

create table sessions (
  id_hash         bytea primary key check (octet_length(id_hash) = 32),  -- sha256(cookie token)
  user_id         uuid not null references users(id) on delete cascade,
  auth_method     text not null check (auth_method in ('password','google','entra','magic_link')),
  created_at      timestamptz not null default now(),
  last_seen_at    timestamptz not null default now(),                    -- written at most every 5 min
  idle_expires_at timestamptz not null,                                  -- sliding 30 days
  abs_expires_at  timestamptz not null,                                  -- hard 90 days
  user_agent      text check (char_length(user_agent) <= 200)
);
create index sessions_user_ix on sessions (user_id);
create index sessions_expiry_ix on sessions (idle_expires_at);

create table guest_tokens (
  token_hash   bytea primary key check (octet_length(token_hash) = 32),
  principal_id uuid not null references principals(id) on delete cascade,
  expires_at   timestamptz not null                                      -- 180 days, slid at most daily
);
create index guest_tokens_principal_ix on guest_tokens (principal_id);

create table auth_tokens (                    -- magic link, password reset, email verification
  token_hash  bytea primary key check (octet_length(token_hash) = 32),
  purpose     text not null check (purpose in ('magic_link','password_reset','verify_email')),
  user_id     uuid references users(id) on delete cascade,                -- null: magic link for a new email
  email       text not null,
  expires_at  timestamptz not null,
  consumed_at timestamptz,
  created_at  timestamptz not null default now()
);
create index auth_tokens_expiry_ix on auth_tokens (expires_at);

create table email_outbox (                   -- written in the same tx as the token; row deleted after send
  id              bigint generated always as identity primary key,
  to_address      text not null,
  subject         text not null check (subject !~ '[\r\n]'),
  body_text       text,                       -- nulled after the final failed attempt (live link must not linger, S23)
  attempts        int not null default 0,
  next_attempt_at timestamptz not null default now(),
  last_error      text,
  created_at      timestamptz not null default now()
);
create index email_outbox_due_ix on email_outbox (next_attempt_at);

------------------------------------------------------------------------------- teams
create table teams (
  id                         uuid primary key,
  name                       text not null check (char_length(name) between 1 and 100),
  description                text check (char_length(description) <= 500),
  created_by                 uuid references principals(id) on delete set null,
  point_descriptions         jsonb not null default '{}' check (jsonb_typeof(point_descriptions) = 'object'),
  max_endorsements_per_board smallint not null default 3 check (max_endorsements_per_board between 0 and 50),
  deleted_at                 timestamptz,
  created_at                 timestamptz not null default now(),
  updated_at                 timestamptz not null default now()
);

create table team_members (
  team_id   uuid not null references teams(id) on delete cascade,
  user_id   uuid not null references users(id) on delete cascade,
  role      text not null check (role in ('owner','admin','member')),
  joined_at timestamptz not null default now(),
  primary key (team_id, user_id)
);
create unique index team_members_one_owner_uq on team_members (team_id) where role = 'owner';
create index team_members_user_ix on team_members (user_id);

create table team_favorites (
  user_id    uuid not null references users(id) on delete cascade,
  team_id    uuid not null references teams(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (user_id, team_id)
);

create table team_invites (
  id          uuid primary key,
  team_id     uuid not null references teams(id) on delete cascade,
  kind        text not null check (kind in ('email','link')),
  email       text check (email = lower(email)),
  role        text not null default 'member' check (role in ('member','admin')),
  token_hash  bytea not null unique check (octet_length(token_hash) = 32),
  token_box   bytea,                         -- link invites only: AES-GCM(token), AAD = invite id; admins can re-copy
  created_by  uuid references principals(id) on delete set null,
  created_at  timestamptz not null default now(),
  expires_at  timestamptz not null,
  revoked_at  timestamptz,
  accepted_at timestamptz,                   -- email invites: single use
  accepted_by uuid references users(id) on delete set null,
  use_count   int not null default 0,
  check ((kind = 'email') = (email is not null)),
  check ((kind = 'link') = (token_box is not null)),
  check (kind = 'email' or role = 'member')    -- link invites never grant admin (S19)
);
create index team_invites_team_ix on team_invites (team_id) where revoked_at is null;

create table team_jira_config (
  team_id         uuid primary key references teams(id) on delete cascade,
  site_host       text not null check (site_host ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$'),
  credentials_box bytea not null,            -- AES-256-GCM({"email","token"}), AAD = "jira:v1:" || team_id
  key_id          text not null,             -- keyring id, for rotation
  token_last4     text not null check (char_length(token_last4) = 4),
  email_masked    text not null,             -- "j•••@acme.com", display only
  project_key     text check (project_key ~ '^[A-Z][A-Z0-9_]{0,19}$'),
  board_id        bigint check (board_id > 0),
  ticket_prefix   text check (ticket_prefix ~ '^[A-Z][A-Z0-9_]{0,19}$'),
  points_field_id text check (points_field_id ~ '^customfield_[0-9]{1,8}$'),
  points_field_overridden boolean not null default false,
  sprint_field_id text check (sprint_field_id ~ '^customfield_[0-9]{1,8}$'),
  status          text not null default 'needs_test'
                  check (status in ('ok','needs_test','auth_failed','unreachable','rotate_required')),  -- imported → rotate_required (S10)
  members_can_edit boolean not null default true,   -- admin toggle: members may write to Jira issues (S33)
  last_checked_at timestamptz,
  updated_by      uuid references principals(id) on delete set null,
  updated_at      timestamptz not null default now()
);

create table recent_activity (
  user_id     uuid not null references users(id) on delete cascade,
  entity_kind text not null check (entity_kind in ('team','board','poker')),
  entity_id   uuid not null,
  last_at     timestamptz not null default now(),
  primary key (user_id, entity_kind, entity_id)
);
create index recent_activity_user_ix on recent_activity (user_id, last_at desc);

------------------------------------------------------------------------------- realtime infrastructure
create table client_cursors (                 -- exactly-once command application per (room, tab)
  room_key     text not null,                 -- 'poker:<uuid>' | 'board:<uuid>' | 'team:<uuid>'
  client_id    uuid not null,
  principal_id uuid not null,                 -- cid is bound to one principal; another principal's use is refused (S16)
  last_cseq    bigint not null check (last_cseq > 0),
  updated_on   date not null default current_date,   -- day granularity, unindexed: updates stay HOT (F23)
  primary key (room_key, client_id)
);
-- upsert: on conflict do update set last_cseq = greatest(client_cursors.last_cseq, excluded.last_cseq),
--         updated_on = excluded.updated_on   (never regresses, R28)

------------------------------------------------------------------------------- poker
create table poker_sessions (
  id                  uuid primary key,
  code                text not null check (code ~ '^[A-Za-z0-9-]{4,36}$'),   -- imported codes verbatim (incl. uuid text)
  team_id             uuid references teams(id) on delete cascade,             -- null = quick session
  created_by          uuid references principals(id) on delete set null,
  presence_enabled    boolean not null default true,
  follow_spotlight    boolean not null default true,
  spotlight_holder_id uuid,                                                    -- FK below: NO ACTION DEFERRABLE (R9)
  spotlight_client_id uuid,                                                    -- tab that may move the spotlight
  spotlight_round_id  uuid,
  current_round_id    uuid,                                                    -- "latest" pointer
  next_round_number   int not null default 1 check (next_round_number >= 1),
  last_activity_at    timestamptz not null default now(),                      -- bumped at most once a minute
  created_at          timestamptz not null default now(),
  check ((spotlight_holder_id is null) = (spotlight_round_id is null))
);
create unique index poker_sessions_code_uq on poker_sessions (code);
create index poker_sessions_code_ci_ix on poker_sessions (upper(code));
create index poker_sessions_team_ix on poker_sessions (team_id, created_at desc) where team_id is not null;
create index poker_sessions_quick_idle_ix on poker_sessions (last_activity_at) where team_id is null;

create table poker_observers (
  session_id   uuid not null references poker_sessions(id) on delete cascade,
  principal_id uuid not null references principals(id) on delete cascade,
  primary key (session_id, principal_id)
);

create table poker_rounds (
  id               uuid primary key,
  session_id       uuid not null references poker_sessions(id) on delete cascade,
  number           int not null check (number >= 1),                        -- stable "Round N" label, server-allocated
  sort_key         int not null,
  ticket_key       text check (ticket_key ~ '^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,8}$'),  -- null = placeholder
  parent_key       text check (parent_key ~ '^[A-Z][A-Z0-9_]{0,19}-[1-9][0-9]{0,8}$'),  -- key only, never a title
  state            text not null default 'voting' check (state in ('voting','revealed')),
  is_active        boolean not null default true,
  is_pending       boolean not null default false,
  pending_owner_id uuid references principals(id) on delete set null,
  version          int not null default 1,                                  -- precondition for setTicket (R5)
  auto_reveal      boolean not null default true,
  winner_points    smallint check (winner_points in (0,1,2,3,5,8,13,21)),  -- plurality; 0 = everyone abstained
  revealed_at      timestamptz,
  revealed_by      uuid references principals(id) on delete set null,       -- null with revealed_at set = auto
  jira_points      numeric(6,2) check (jira_points between 0 and 1000),     -- last points written to Jira (decimal, F15)
  jira_points_at   timestamptz,
  jira_points_by   uuid references principals(id) on delete set null,
  created_at       timestamptz not null default now(),
  constraint poker_rounds_number_uq unique (session_id, number),
  constraint poker_rounds_sort_uq unique (session_id, sort_key) deferrable initially deferred,
  check (state = 'revealed' or (winner_points is null and revealed_at is null)),
  check (not is_pending or pending_owner_id is not null)
);
alter table poker_sessions
  add constraint poker_sessions_current_fk foreign key (current_round_id)
      references poker_rounds(id) on delete set null deferrable initially deferred,
  -- Spotlight FKs are NO ACTION DEFERRABLE (R9): a SET NULL action fires at statement end even when
  -- deferrable and would null one column, violating the holder/round CHECK. The store must repair the
  -- spotlight explicitly (clearAll, delete, principal deletion); a missed repair fails loudly at COMMIT.
  add constraint poker_sessions_spot_fk foreign key (spotlight_round_id)
      references poker_rounds(id) deferrable initially deferred,
  add constraint poker_sessions_spot_holder_fk foreign key (spotlight_holder_id)
      references principals(id) deferrable initially deferred;

create table poker_votes (                    -- pre-reveal rows persisted for crash safety, never projected to others
  round_id     uuid not null references poker_rounds(id) on delete cascade,
  principal_id uuid not null references principals(id) on delete cascade,
  points       smallint check (points in (1,2,3,5,8,13,21)),
  high_points  smallint check (high_points in (2,3,5,8,13,21)),           -- "between": adjacency checked in Core
  locked       boolean not null default false,
  abstained    boolean not null default false,
  updated_at   timestamptz not null default now(),
  primary key (round_id, principal_id),
  check (abstained = (points is null)),
  check (not abstained or (locked and high_points is null)),
  check (high_points is null or high_points > points)
);

------------------------------------------------------------------------------- retro
create table retro_boards (
  id                   uuid primary key,
  code                 text not null check (code ~ '^[A-Za-z0-9-]{3,64}$'),   -- new: 10-char Crockford (S9); imported verbatim
  team_id              uuid references teams(id) on delete cascade,
  title                text not null check (char_length(title) between 1 and 200),
  created_by           uuid references principals(id) on delete set null,
  visibility           text not null check (visibility in ('link','team','password')),
  password_hash        text check (password_hash ~ '^\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}$'),
  password_set_at      timestamptz,
  stage                text not null default 'thinking' check (stage in ('thinking','voting','discussing','closed')),
  focus_card_id        uuid,
  -- config (1:1, inlined)
  allow_anonymous      boolean not null default true,
  voting_enabled       boolean not null default true,
  max_votes_per_user   smallint check (max_votes_per_user between 1 and 99),   -- null = unlimited
  show_author_names    boolean not null default true,
  stages_enabled       boolean not null default false,
  enforce_readiness    boolean not null default false,
  allow_self_votes     boolean not null default true,
  vote_emoji           text not null default '👍' check (char_length(vote_emoji) between 1 and 8),
  sort_chronologically boolean not null default false,
  -- timer (server clock)
  timer_state          text not null default 'idle' check (timer_state in ('idle','running','paused')),
  timer_duration_ms    int not null default 900000 check (timer_duration_ms between 1000 and 7200000),
  timer_ends_at        timestamptz,
  timer_remaining_ms   int check (timer_remaining_ms >= 0),
  timer_alarm          boolean not null default true,
  timer_music          boolean not null default false,
  archived_at          timestamptz,
  archived_by          uuid references principals(id) on delete set null,
  deleted_at           timestamptz,
  last_activity_at     timestamptz not null default now(),
  created_at           timestamptz not null default now(),
  updated_at           timestamptz not null default now(),
  check ((visibility = 'password') = (password_hash is not null)),
  check ((password_hash is null) = (password_set_at is null)),
  check (visibility <> 'team' or team_id is not null),
  check ((timer_state = 'running') = (timer_ends_at is not null)),
  check ((timer_state = 'paused') = (timer_remaining_ms is not null))
);
create unique index retro_boards_code_uq on retro_boards (code);
create index retro_boards_code_ci_ix on retro_boards (upper(code));
create index retro_boards_team_ix on retro_boards (team_id, created_at desc) where team_id is not null and deleted_at is null;
create index retro_boards_quick_idle_ix on retro_boards (last_activity_at) where team_id is null;
alter table blobs add constraint blobs_board_fk foreign key (board_id) references retro_boards(id) on delete cascade;

create table board_access_grants (            -- password unlocked by this principal
  board_id       uuid not null references retro_boards(id) on delete cascade,
  principal_id   uuid not null references principals(id) on delete cascade,
  password_epoch timestamptz not null,        -- = retro_boards.password_set_at at grant time; a change invalidates
  granted_at     timestamptz not null default now(),
  primary key (board_id, principal_id)
);

create table retro_columns (
  id              uuid primary key,
  board_id        uuid not null references retro_boards(id) on delete cascade,
  title           text not null check (char_length(title) between 1 and 100),
  color           text not null check (color in ('green','red','blue','yellow','purple','pink','indigo','orange','gray')),
  sort_key        int not null,
  is_action_items boolean not null default false,
  version         int not null default 1,     -- precondition for column.update (R5)
  created_at      timestamptz not null default now(),
  constraint retro_columns_sort_uq unique (board_id, sort_key) deferrable initially deferred
);
create unique index retro_columns_one_action_uq on retro_columns (board_id) where is_action_items;

create table retro_cards (
  id            uuid primary key,
  board_id      uuid not null references retro_boards(id) on delete cascade,
  column_id     uuid not null references retro_columns(id) on delete cascade,
  body          text not null check (char_length(body) between 1 and 8000),   -- Retro Markup, never HTML
  author_id     uuid references principals(id) on delete set null,
  anon_tag      bytea check (anon_tag is null or octet_length(anon_tag) = 32),  -- HMAC(k_anon, board || principal)
  legacy_author text check (char_length(legacy_author) <= 100),                 -- imported label when no principal
  version       int not null default 1,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  check (num_nonnulls(author_id, anon_tag) <= 1)
);
create index retro_cards_board_ix on retro_cards (board_id);
create index retro_cards_column_ix on retro_cards (column_id);
alter table retro_boards add constraint retro_boards_focus_fk
  foreign key (focus_card_id) references retro_cards(id) on delete set null deferrable initially deferred;

create table retro_votes (
  card_id    uuid not null references retro_cards(id) on delete cascade,
  voter_id   uuid not null references principals(id) on delete cascade,
  board_id   uuid not null references retro_boards(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (card_id, voter_id)
);
create index retro_votes_board_voter_ix on retro_votes (board_id, voter_id);

create table retro_comments (
  id            uuid primary key,
  card_id       uuid not null references retro_cards(id) on delete cascade,
  board_id      uuid not null references retro_boards(id) on delete cascade,
  body          text not null check (char_length(body) between 1 and 4000),
  author_id     uuid references principals(id) on delete set null,
  anon_tag      bytea check (anon_tag is null or octet_length(anon_tag) = 32),
  legacy_author text check (char_length(legacy_author) <= 100),
  created_at    timestamptz not null default now(),
  check (num_nonnulls(author_id, anon_tag) <= 1)
);
create index retro_comments_card_ix on retro_comments (card_id, created_at);
create index retro_comments_board_ix on retro_comments (board_id);

create table retro_readiness (
  board_id     uuid not null references retro_boards(id) on delete cascade,
  stage        text not null check (stage in ('thinking','voting','discussing','closed')),
  principal_id uuid not null references principals(id) on delete cascade,
  ready_at     timestamptz not null default now(),
  primary key (board_id, stage, principal_id)
);

create table mentions (                       -- derived from markup on every card/comment write
  source_kind text not null check (source_kind in ('card','comment')),
  source_id   uuid not null,                  -- card or comment id (cleaned by the store on delete)
  board_id    uuid not null references retro_boards(id) on delete cascade,
  user_id     uuid not null references users(id) on delete cascade,
  created_at  timestamptz not null default now(),
  primary key (source_kind, source_id, user_id)
);
create index mentions_user_ix on mentions (user_id, created_at desc);

create table board_templates (
  id                 uuid primary key,
  team_id            uuid not null references teams(id) on delete cascade,
  name               text not null check (char_length(name) between 1 and 100),
  is_default         boolean not null default false,
  allow_anonymous    boolean not null default true,
  voting_enabled     boolean not null default true,
  max_votes_per_user smallint check (max_votes_per_user between 1 and 99),
  show_author_names  boolean not null default true,
  stages_enabled     boolean not null default false,
  enforce_readiness  boolean not null default false,
  allow_self_votes   boolean not null default true,
  vote_emoji         text not null default '👍' check (char_length(vote_emoji) between 1 and 8),
  created_at         timestamptz not null default now(),
  updated_at         timestamptz not null default now()
);
create unique index board_templates_one_default_uq on board_templates (team_id) where is_default;
create index board_templates_team_ix on board_templates (team_id);

create table template_columns (
  id              uuid primary key,
  template_id     uuid not null references board_templates(id) on delete cascade,
  title           text not null check (char_length(title) between 1 and 100),
  color           text not null check (color in ('green','red','blue','yellow','purple','pink','indigo','orange','gray')),
  sort_key        int not null,
  is_action_items boolean not null default false,
  unique (template_id, sort_key)
);
create unique index template_columns_one_action_uq on template_columns (template_id) where is_action_items;

------------------------------------------------------------------------------- team action items & endorsements
create table team_action_items (
  id              uuid primary key,
  team_id         uuid not null references teams(id) on delete cascade,
  text            text not null check (char_length(text) between 1 and 8000),   -- markup, mirrors the source card
  source_board_id uuid references retro_boards(id) on delete set null,
  source_card_id  uuid references retro_cards(id) on delete set null,
  source_version  int not null default 0,     -- monotonic copy guard from retro_cards.version
  assigned_to     uuid references users(id) on delete set null,
  done_at         timestamptz,
  done_by         uuid references principals(id) on delete set null,
  created_by      uuid references principals(id) on delete set null,   -- NULL when the source card is anonymous (S6)
  created_at      timestamptz not null default now()
);
-- Column-scoped writes only (R24): BoardActor's card/column deletes run ON DELETE SET NULL on source_card_id,
-- so TeamStore never writes whole rows from memory; it UPDATEs exactly the columns a command changed.
create unique index team_action_items_source_uq on team_action_items (source_card_id) where source_card_id is not null;
create index team_action_items_open_ix on team_action_items (team_id, created_at desc) where done_at is null;
create index team_action_items_team_ix on team_action_items (team_id, created_at desc);
create index team_action_items_assignee_ix on team_action_items (assigned_to) where assigned_to is not null;

create table endorsement_types (
  id          uuid primary key,
  team_id     uuid not null references teams(id) on delete cascade,
  name        text not null check (char_length(name) between 1 and 60),
  description text check (char_length(description) <= 200),
  emoji       text not null check (char_length(emoji) between 1 and 8),
  sort_key    int not null,
  is_default  boolean not null default false,
  archived_at timestamptz,                    -- "delete" archives, keeping history
  created_at  timestamptz not null default now()
);
create index endorsement_types_team_ix on endorsement_types (team_id, sort_key);

create table endorsements (
  id           uuid primary key,
  board_id     uuid not null references retro_boards(id) on delete cascade,
  team_id      uuid not null references teams(id) on delete cascade,
  type_id      uuid not null references endorsement_types(id) on delete cascade,
  from_user_id uuid not null references users(id) on delete cascade,
  to_user_id   uuid not null references users(id) on delete cascade,
  created_at   timestamptz not null default now(),
  unique (board_id, from_user_id, to_user_id, type_id),
  check (from_user_id <> to_user_id)
);
create index endorsements_team_ix on endorsements (team_id, created_at desc);
create index endorsements_to_ix on endorsements (to_user_id, created_at desc);

------------------------------------------------------------------------------- audit (keys and actions only)
create table audit_log (
  id       bigint generated always as identity primary key,
  at       timestamptz not null default now(),
  actor_id uuid,
  team_id  uuid,
  action   text not null,      -- 'jira.points', 'jira.transition', 'team.role.set', 'admin.user.disable', 'auth.login.fail'…
  target   text,               -- issue key, user id, board id
  meta     jsonb not null default '{}'   -- ids, codes, counts, email_hmac (HKDF subkey 'audit-email', S22);
                                         -- never content, tokens, passwords or plain email hashes
);
create index audit_log_at_ix on audit_log (at);
create index audit_log_team_ix on audit_log (team_id, at desc) where team_id is not null;

------------------------------------------------------------------------------- archive (importer-owned)
create table archive.import_runs (
  id                 uuid primary key,
  started_at         timestamptz not null default now(),
  finished_at        timestamptz,
  status             text not null check (status in ('running','succeeded','failed')),
  source_fingerprint text not null,          -- sha256(system_identifier || server_version || per-table counts)
  seal_key_id        text,                   -- fingerprint of the offline public key; null = secrets stripped
  report             jsonb
);

create table archive.rows (                  -- dropped tables, and dropped columns of kept tables
  source_table text not null,                -- 'public.poker_session_chat', 'public.teams#dropped', 'auth.users#meta'
  source_pk    text not null,                -- canonical pk text; composite pks joined with '|'
  row          jsonb not null,               -- secret fields replaced by {"sealed":"<b64>"} or {"stripped":true}
  import_run   uuid not null references archive.import_runs(id),
  primary key (source_table, source_pk)
);

create table archive.storage_objects (       -- manifest of every Supabase storage object
  bucket      text not null,
  path        text not null,
  byte_size   bigint not null,
  sha256      bytea,
  disposition text not null check (disposition in ('imported','manifest_only','skipped')),
  blob_id     uuid,
  import_run  uuid not null references archive.import_runs(id),
  primary key (bucket, path)
);

create table archive.storage_cache (         -- staged downloads, committed BEFORE the main import tx (S2); makes
  bucket text not null,                      -- storage resumable across runs (the importer image has no volume)
  path   text not null,
  sha256 bytea not null,
  bytes  bytea not null,
  primary key (bucket, path)
);

create table archive.excluded_tables (       -- foreign / non-Retroscope data: counted, never copied
  source_table text primary key,
  row_count    bigint not null,
  reason       text not null,
  import_run   uuid not null references archive.import_runs(id)
);
