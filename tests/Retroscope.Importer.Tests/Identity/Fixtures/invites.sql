-- Invitations: a live email invite, a live link invite past the 7-day cap, expired, accepted and inactive ones,
-- and a dangling inviter.
set session_replication_role = replica;

insert into auth.users (id, email, last_sign_in_at, created_at, updated_at) values
  ('00000000-0000-0000-0000-0000000000a1', 'alice@example.com', '2026-01-01', '2025-01-01', '2025-01-01');

insert into public.teams (id, name, creator_id, created_at, updated_at) values
  ('00000000-0000-0000-0000-0000000000c1', 'Alpha', '00000000-0000-0000-0000-0000000000a1', '2025-01-01', '2025-01-01');

insert into public.team_members (id, team_id, user_id, role, joined_at) values
  ('00000000-0000-0000-0000-000000000d01', '00000000-0000-0000-0000-0000000000c1', '00000000-0000-0000-0000-0000000000a1', 'owner', '2025-01-01');

insert into public.team_invitations (id, team_id, email, invited_by, token, status, expires_at, created_at, invite_type, is_active) values
  ('00000000-0000-0000-0000-000000000501', '00000000-0000-0000-0000-0000000000c1', 'New.Person@Example.com', '00000000-0000-0000-0000-0000000000a1', 'email-token-1', 'pending', '2099-01-01', '2025-01-01', 'email', true),
  ('00000000-0000-0000-0000-000000000502', '00000000-0000-0000-0000-0000000000c1', 'link@invite', '00000000-0000-0000-0000-0000000000a1', 'link-token-1', 'pending', '2099-01-01', '2025-01-01', 'link', true),
  ('00000000-0000-0000-0000-000000000503', '00000000-0000-0000-0000-0000000000c1', 'old@example.com', '00000000-0000-0000-0000-0000000000a1', 'expired-token', 'pending', '2000-01-01', '1999-12-25', 'email', true),
  ('00000000-0000-0000-0000-000000000504', '00000000-0000-0000-0000-0000000000c1', 'joined@example.com', '00000000-0000-0000-0000-0000000000a1', 'accepted-token', 'accepted', '2099-01-01', '2025-01-01', 'email', true),
  ('00000000-0000-0000-0000-000000000505', '00000000-0000-0000-0000-0000000000c1', 'link@invite', '00000000-0000-0000-0000-0000000000a1', 'inactive-token', 'pending', '2099-01-01', '2025-01-01', 'link', false),
  ('00000000-0000-0000-0000-000000000506', '00000000-0000-0000-0000-0000000000c1', 'other@example.com', '00000000-0000-0000-0000-0000000000a6', 'email-token-2', 'pending', '2099-01-01', '2025-01-01', null, true);
