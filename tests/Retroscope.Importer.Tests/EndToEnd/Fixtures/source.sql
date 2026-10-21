-- One small Supabase project that touches every stage: accounts (Google-verified, merged duplicate, guest, phone-only),
-- avatars (own object, Google, someone else's object, an upload the profile never used), a team with Jira and one
-- without members, invites, templates, endorsements, a team and a quick poker session, a team and a quick board with
-- anonymous posts carrying images, action items, archive-only tables and a foreign table.
set session_replication_role = replica;

insert into auth.users (id, email, encrypted_password, last_sign_in_at, created_at, updated_at, phone, is_anonymous) values
  ('a1000000-0000-4000-8000-000000000001', 'Alice@Example.com', '$2a$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi', '2026-01-01', '2025-01-01', '2025-01-01', null, false),
  ('b0b00000-0000-4000-8000-000000000001', 'bob@example.com', '$2b$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi', '2026-01-01', '2025-01-01', '2025-01-01', null, false),
  ('ca401000-0000-4000-8000-000000000001', 'carol@example.com', 'plaintext-not-bcrypt', '2026-01-01', '2025-01-01', '2025-01-01', null, false),
  ('d0000000-0000-4000-8000-000000000001', 'Dave@example.com', '$2a$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi', '2025-06-01', '2024-01-01', '2024-01-01', null, false),
  ('d0000000-0000-4000-8000-000000000002', 'dave@example.com', null, '2026-02-01', '2025-01-01', '2025-01-01', null, false),
  ('9e000000-0000-4000-8000-000000000001', null, null, null, '2025-01-01', '2025-01-01', null, true),
  ('f0000000-0000-4000-8000-000000000001', null, null, null, '2025-01-01', '2025-01-01', '+15550001', false);

insert into auth.identities (id, provider_id, user_id, identity_data, provider, created_at) values
  ('1d000000-0000-4000-8000-000000000001', 'google-alice', 'a1000000-0000-4000-8000-000000000001', '{"email": "alice@example.com"}', 'google', '2025-01-01'),
  ('1d000000-0000-4000-8000-000000000002', 'b0b00000-0000-4000-8000-000000000001', 'b0b00000-0000-4000-8000-000000000001', '{"email": "bob@example.com"}', 'email', '2025-01-01');

insert into public.profiles (id, full_name, nickname, avatar_url, theme_preference, role, created_at, updated_at) values
  ('a1000000-0000-4000-8000-000000000001', 'Alice Liddell', 'Alice', 'https://project.supabase.example/storage/v1/object/public/avatars/a1000000-0000-4000-8000-000000000001.png?t=1', 'dark', 'admin', '2025-01-01', '2025-01-01'),
  ('b0b00000-0000-4000-8000-000000000001', 'Bob Builder', null, 'https://lh3.googleusercontent.com/a/bob', 'light', 'user', '2025-01-01', '2025-01-01'),
  ('ca401000-0000-4000-8000-000000000001', 'Carol', null, 'https://project.supabase.example/storage/v1/object/public/avatars/a1000000-0000-4000-8000-000000000001.png', 'light', 'admin', '2025-01-01', '2025-01-01'),
  ('d0000000-0000-4000-8000-000000000001', 'Old Dave', null, null, 'light', 'user', '2024-01-01', '2024-01-01'),
  ('d0000000-0000-4000-8000-000000000002', 'Dave', null, null, 'light', 'user', '2025-01-01', '2025-01-01'),
  ('9e000000-0000-4000-8000-000000000001', 'guest@corp.example.org', null, null, 'light', 'user', '2025-01-01', '2025-01-01');

insert into storage.buckets (id, name, public) values ('avatars', 'avatars', true), ('chat-images', 'chat-images', true) on conflict do nothing;
insert into storage.objects (id, bucket_id, name, metadata) values
  ('0b000000-0000-4000-8000-000000000001', 'avatars', 'a1000000-0000-4000-8000-000000000001.png', '{"size": 9}'),
  ('0b000000-0000-4000-8000-000000000002', 'avatars', 'ca401000-0000-4000-8000-000000000001.png', '{"size": 9}'),
  ('0b000000-0000-4000-8000-000000000003', 'avatars', 'shared/logo.png', '{"size": 120}'),
  ('0b000000-0000-4000-8000-000000000004', 'chat-images', 'session/picture.png', '{"size": 2048}');

insert into public.teams (id, name, description, creator_id, created_at, updated_at, jira_domain, jira_email, jira_api_key, jira_ticket_prefix, jira_board_id, poker_point_value_descriptions) values
  ('7ea70000-0000-4000-8000-000000000001', 'Alpha', 'The team', 'a1000000-0000-4000-8000-000000000001', '2025-01-01', '2025-01-02',
   'acme.atlassian.net', 'jira@acme.com', 'secret-token-1234', 'ALP', '42', '{"3": "a few days"}'),
  ('7ea70000-0000-4000-8000-000000000002', 'Beta', null, 'f0000000-0000-4000-8000-000000000001', '2025-01-01', '2025-01-01',
   null, null, null, null, null, '{}');

insert into public.team_members (id, team_id, user_id, role, joined_at) values
  ('3e000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', 'owner', '2025-01-01'),
  ('3e000000-0000-4000-8000-000000000002', '7ea70000-0000-4000-8000-000000000001', 'b0b00000-0000-4000-8000-000000000001', 'member', '2025-01-02'),
  ('3e000000-0000-4000-8000-000000000003', '7ea70000-0000-4000-8000-000000000001', 'd0000000-0000-4000-8000-000000000001', 'admin', '2025-01-03'),
  ('3e000000-0000-4000-8000-000000000004', '7ea70000-0000-4000-8000-000000000002', 'f0000000-0000-4000-8000-000000000001', 'owner', '2025-01-01');

insert into public.team_invitations (id, team_id, email, invited_by, token, status, expires_at, created_at, invite_type, is_active) values
  ('11000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'link@invite', 'a1000000-0000-4000-8000-000000000001', 'link-token-1', 'pending', '2099-01-01', '2025-01-01', 'link', true),
  ('11000000-0000-4000-8000-000000000002', '7ea70000-0000-4000-8000-000000000001', 'New@Example.com', 'a1000000-0000-4000-8000-000000000001', 'email-token-1', 'pending', '2099-01-01', '2025-01-01', 'email', true),
  ('11000000-0000-4000-8000-000000000003', '7ea70000-0000-4000-8000-000000000001', 'old@example.com', 'a1000000-0000-4000-8000-000000000001', 'old-token', 'accepted', '2099-01-01', '2025-01-01', 'email', true);

insert into public.user_favorite_teams (id, user_id, team_id, created_at) values
  ('fa000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', '2025-01-01');

insert into public.board_templates (id, team_id, name, is_default, created_at, updated_at, max_votes_per_user, vote_emoji, retro_stages_enabled) values
  ('7e300000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'Classic', true, '2025-01-01', '2025-01-01', 3, '🔥', true);
insert into public.template_columns (id, template_id, title, color, position, created_at, is_action_items) values
  ('7e310000-0000-4000-8000-000000000001', '7e300000-0000-4000-8000-000000000001', 'Good', 'bg-green-100 border-green-300', 0, '2025-01-01', false),
  ('7e310000-0000-4000-8000-000000000002', '7e300000-0000-4000-8000-000000000001', 'Actions', 'bg-purple-100 border-purple-300', 1, '2025-01-01', true);

insert into public.endorsement_types (id, team_id, name, description, icon_url, position, is_default, created_at, updated_at) values
  ('e7000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'Helper', 'Always there', '🙌', 1, false, '2025-01-01', '2025-01-01');
insert into public.endorsement_settings (id, team_id, max_endorsements_per_user_per_board, created_at, updated_at) values
  ('e7100000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 5, '2025-01-01', '2025-01-01');

insert into public.retro_boards (id, room_id, title, is_private, password_hash, creator_id, team_id, archived, deleted, retro_stage, created_at, updated_at) values
  ('b0a40000-0000-4000-8000-000000000001', 'TEAMRETRO', 'Sprint retro', false, null, 'a1000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', false, false, 'voting', '2026-01-01', '2026-01-02'),
  ('b0a40000-0000-4000-8000-000000000002', 'QUICKRETRO', 'Quick retro', true, 'hunter2', 'b0b00000-0000-4000-8000-000000000001', null, false, false, 'thinking', '2026-01-01', '2026-01-01');

insert into public.retro_board_config (board_id, allow_anonymous, voting_enabled, max_votes_per_user, show_author_names,
    retro_stages_enabled, enforce_stage_readiness, allow_self_votes, vote_emoji, sort_chronologically, timer_duration_seconds,
    timer_alarm_enabled, timer_music_enabled) values
  ('b0a40000-0000-4000-8000-000000000001', true, true, 3, true, true, false, true, '👍', false, 300, true, false);

insert into public.retro_columns (id, board_id, title, color, position, sort_order, is_action_items, created_at) values
  ('c0100000-0000-4000-8000-000000000001', 'b0a40000-0000-4000-8000-000000000001', 'Good', 'bg-green-100 border-green-300', 0, 0, false, '2026-01-01'),
  ('c0100000-0000-4000-8000-000000000002', 'b0a40000-0000-4000-8000-000000000001', 'Actions', 'bg-yellow-100', 1, 0, true, '2026-01-01'),
  ('c0100000-0000-4000-8000-000000000003', 'b0a40000-0000-4000-8000-000000000002', 'Notes', 'bg-blue-100', 0, 0, false, '2026-01-01');

insert into public.retro_items (id, board_id, column_id, text, author, author_id, session_id, votes, created_at, updated_at) values
  ('17e00000-0000-4000-8000-000000000001', 'b0a40000-0000-4000-8000-000000000001', 'c0100000-0000-4000-8000-000000000002',
   '<p>fix the <strong>build</strong> <img src="data:image/png;base64,iVBORw0KGgqq"></p>', 'Anonymous', 'a1000000-0000-4000-8000-000000000001', 'sess-alice', 1, '2026-01-01 10:00', '2026-01-01 10:00'),
  ('17e00000-0000-4000-8000-000000000002', 'b0a40000-0000-4000-8000-000000000001', 'c0100000-0000-4000-8000-000000000001',
   '<p>thanks [[mention:a1000000-0000-4000-8000-000000000001:Alice]] <img src="data:image/png;base64,iVBORw0KGgq7"></p>', 'Bob', 'b0b00000-0000-4000-8000-000000000001', null, 2, '2026-01-01 10:01', '2026-01-01 10:01'),
  ('17e00000-0000-4000-8000-000000000003', 'b0a40000-0000-4000-8000-000000000001', 'c0100000-0000-4000-8000-000000000001',
   '<p>from a guest</p>', 'Sneaky Penguin', null, 'sess-guest', 0, '2026-01-01 10:02', '2026-01-01 10:02'),
  ('17e00000-0000-4000-8000-000000000004', 'b0a40000-0000-4000-8000-000000000002', 'c0100000-0000-4000-8000-000000000003',
   'plain old text', 'Old Name', null, null, 0, '2026-01-01 10:03', '2026-01-01 10:03');

insert into public.retro_comments (id, item_id, author, author_id, text, session_id, created_at, updated_at) values
  ('c0aa0000-0000-4000-8000-000000000001', '17e00000-0000-4000-8000-000000000002', 'Anonymous', 'b0b00000-0000-4000-8000-000000000001',
   '<p>quietly <img src="data:image/png;base64,iVBORw0KGgq7"></p>', 'sess-bob', '2026-01-01 11:00', '2026-01-01 11:00'),
  ('c0aa0000-0000-4000-8000-000000000002', '17e00000-0000-4000-8000-000000000001', 'Alice', 'a1000000-0000-4000-8000-000000000001',
   '<p>agreed</p>', null, '2026-01-01 11:01', '2026-01-01 11:01');

insert into public.retro_votes (id, item_id, user_id, session_id, board_id, created_at) values
  ('5f000000-0000-4000-8000-000000000001', '17e00000-0000-4000-8000-000000000001', 'b0b00000-0000-4000-8000-000000000001', null, 'b0a40000-0000-4000-8000-000000000001', '2026-01-01 12:00'),
  ('5f000000-0000-4000-8000-000000000002', '17e00000-0000-4000-8000-000000000002', 'a1000000-0000-4000-8000-000000000001', null, 'b0a40000-0000-4000-8000-000000000001', '2026-01-01 12:01'),
  ('5f000000-0000-4000-8000-000000000003', '17e00000-0000-4000-8000-000000000002', null, 'sess-guest', null, '2026-01-01 12:02');

insert into public.retro_user_readiness (board_id, user_id, session_id, current_stage, is_ready, created_at, updated_at) values
  ('b0a40000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', null, 'voting', true, '2026-01-01', '2026-01-01 13:00');

insert into public.team_action_items (id, team_id, text, source_board_id, source_item_id, created_at, created_by, done, done_at, done_by, assigned_to) values
  ('ac710000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', '<p>fix the <strong>build</strong> <img src="data:image/png;base64,iVBORw0KGgqq"></p>',
   'b0a40000-0000-4000-8000-000000000001', '17e00000-0000-4000-8000-000000000001', '2026-01-02', 'a1000000-0000-4000-8000-000000000001', false, null, null, 'b0b00000-0000-4000-8000-000000000001'),
  ('ac710000-0000-4000-8000-000000000002', '7ea70000-0000-4000-8000-000000000001', '<p>write docs</p>',
   null, null, '2026-01-02', 'b0b00000-0000-4000-8000-000000000001', true, '2026-01-03', 'd0000000-0000-4000-8000-000000000001', null);

insert into public.endorsements (id, board_id, team_id, endorsement_type_id, from_user_id, to_user_id, created_at) values
  ('e7200000-0000-4000-8000-000000000001', 'b0a40000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'e7000000-0000-4000-8000-000000000001',
   'a1000000-0000-4000-8000-000000000001', 'b0b00000-0000-4000-8000-000000000001', '2026-01-01');

insert into public.poker_sessions (id, team_id, room_id, current_round_number, created_at, observer_ids) values
  ('5e000000-0000-4000-8000-000000000001', '7ea70000-0000-4000-8000-000000000001', 'ALPHAPOKER', 2, '2026-01-01', '{d0000000-0000-4000-8000-000000000001}'),
  ('5e000000-0000-4000-8000-000000000002', null, 'QUICKPOKER', 1, '2026-01-01', '{}');

insert into public.poker_session_rounds (id, session_id, round_number, created_at, completed_at, sort_order, game_state, is_active,
                                         is_pending_round, average_points, selections, ticket_number, ticket_parent_key,
                                         ticket_title, ticket_parent_summary, slack_message_ts) values
  ('50000000-0000-4000-8000-000000000001', '5e000000-0000-4000-8000-000000000001', 1, '2026-01-01 10:00', '2026-01-01 10:30', 0, 'Playing', false, false, 5,
   '{"a1000000-0000-4000-8000-000000000001": {"points": 5, "locked": true, "name": "Alice"}, "b0b00000-0000-4000-8000-000000000001": {"points": 8, "locked": true, "name": "Bob"}, "c5000000-0000-4000-8000-000000000001": {"points": 5, "locked": true, "name": "visitor@example.com"}}',
   'ALP-1', 'ALP-100', 'Secret title', 'Secret parent', null),
  ('50000000-0000-4000-8000-000000000002', '5e000000-0000-4000-8000-000000000001', 2, '2026-01-01 11:00', '2026-01-01 11:00', 1, 'Selection', true, false, 0,
   '{"a1000000-0000-4000-8000-000000000001": {"points": 3, "locked": true, "name": "Alice"}, "b0b00000-0000-4000-8000-000000000001": {"points": 1, "locked": false, "name": "Bob"}}',
   'ALP-2', null, 'Another title', null, null),
  ('50000000-0000-4000-8000-000000000003', '5e000000-0000-4000-8000-000000000002', 1, '2026-01-01 12:00', '2026-01-01 12:00', 0, 'Selection', true, false, 0,
   '{"c5000000-0000-4000-8000-000000000001": {"points": 13, "locked": true, "name": "Visitor"}}',
   null, null, null, null, null);

insert into public.user_recent_activity (id, user_id, entity_type, entity_id, last_accessed_at, created_at) values
  ('ad000000-0000-4000-8000-000000000001', 'a1000000-0000-4000-8000-000000000001', 'board', 'b0a40000-0000-4000-8000-000000000001', '2026-01-02', '2026-01-01'),
  ('ad000000-0000-4000-8000-000000000002', 'b0b00000-0000-4000-8000-000000000001', 'poker_session', '5e000000-0000-4000-8000-000000000001', '2026-01-02', '2026-01-01');

insert into public.feature_flags (flag_name, is_enabled) values ('sentiment', true);
insert into public.app_config (key, value) values ('SLACK_SIGNING_SECRET', 'shh'), ('tier_limit', '5');
insert into public.consultation_requests (name, email, phone, property_type, service_type, description)
  values ('n', 'n@example.com', '555', 'house', 'audit', 'private');
