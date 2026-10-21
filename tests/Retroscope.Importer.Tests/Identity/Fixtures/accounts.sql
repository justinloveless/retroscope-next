-- Accounts: Google-verified and unverified users, a non-bcrypt hash, a three-way duplicate email, a phone-only
-- signup, an anonymous guest with an email-shaped name, banned and deleted accounts, and a source admin role.
set session_replication_role = replica;

insert into auth.users (id, email, encrypted_password, last_sign_in_at, created_at, updated_at, phone, banned_until, deleted_at, is_anonymous) values
  ('00000000-0000-0000-0000-0000000000a1', 'Alice@Example.com', '$2a$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi', '2026-01-01', '2025-01-01', '2025-01-02', null, null, null, false),
  ('00000000-0000-0000-0000-0000000000a2', 'bob@example.com', 'not-a-bcrypt-hash', '2026-01-01', '2025-01-01', '2025-01-01', null, null, null, false),
  ('00000000-0000-0000-0000-0000000000a3', 'Dup@Example.com ', '$2b$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi', '2026-01-01', '2025-01-01', '2025-01-01', null, null, null, false),
  ('00000000-0000-0000-0000-0000000000a4', 'dup@example.com', null, '2026-02-01', '2025-02-01', '2025-02-01', null, null, null, false),
  ('00000000-0000-0000-0000-0000000000a5', 'DUP@example.com', null, null, '2024-12-01', '2024-12-01', null, null, null, false),
  ('00000000-0000-0000-0000-0000000000a6', null, null, null, '2025-01-01', '2025-01-01', '+15550001', null, null, false),
  ('00000000-0000-0000-0000-0000000000a7', null, null, null, '2025-01-01', '2025-01-01', null, null, null, true),
  ('00000000-0000-0000-0000-0000000000a8', 'banned@example.com', null, null, '2025-01-01', '2025-01-01', null, '2999-01-01', null, false),
  ('00000000-0000-0000-0000-0000000000a9', 'gone@example.com', null, null, '2025-01-01', '2025-01-01', null, '2001-01-01', '2026-03-01', false);

insert into auth.identities (id, provider_id, user_id, identity_data, provider, created_at) values
  ('00000000-0000-0000-0000-0000000000b1', 'google-alice', '00000000-0000-0000-0000-0000000000a1', '{"email": "ALICE@example.com", "sub": "google-alice"}', 'google', '2025-01-03'),
  ('00000000-0000-0000-0000-0000000000b2', 'google-bob', '00000000-0000-0000-0000-0000000000a2', '{"email": "bob.other@gmail.com"}', 'google', '2025-01-03'),
  ('00000000-0000-0000-0000-0000000000b3', 'google-dup', '00000000-0000-0000-0000-0000000000a3', '{"email": "dup@example.com"}', 'google', null),
  ('00000000-0000-0000-0000-0000000000b4', '00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000a1', '{"email": "alice@example.com"}', 'email', '2025-01-01'),
  ('00000000-0000-0000-0000-0000000000b5', 'google-phone', '00000000-0000-0000-0000-0000000000a6', '{}', 'google', '2025-01-01');

insert into public.profiles (id, full_name, nickname, theme_preference, role, created_at, updated_at, poker_advisor_enabled) values
  ('00000000-0000-0000-0000-0000000000a1', '  Alice Liddell ', 'a nickname that is much longer than forty characters', 'dark', 'admin', '2025-01-01', '2025-01-01', true),
  ('00000000-0000-0000-0000-0000000000a2', 'bob@example.com', '', null, 'admin', '2025-01-01', '2025-01-01', false),
  ('00000000-0000-0000-0000-0000000000a3', 'Old Dup', null, 'light', 'user', '2025-01-01', '2025-01-01', false),
  ('00000000-0000-0000-0000-0000000000a4', 'Kept Dup', 'dupper', 'light', 'user', '2025-02-01', '2025-02-01', false),
  ('00000000-0000-0000-0000-0000000000a6', 'Phone Person', null, 'light', 'user', '2025-01-01', '2025-01-01', false),
  ('00000000-0000-0000-0000-0000000000a7', 'guest@corp.example.org', null, 'light', 'user', '2025-01-01', '2025-01-01', false),
  ('00000000-0000-0000-0000-0000000000ff', 'Orphan', null, 'light', 'user', '2025-01-01', '2025-01-01', false);
