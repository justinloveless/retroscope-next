-- Tables of unrelated applications that share the Supabase project. They are known only from
-- orig/src/integrations/supabase/types.ts (no migration creates them); realtime_demo is already in the dump.

create table public.consultation_requests (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  email text not null,
  phone text not null,
  company text not null default '',
  property_type text not null,
  service_type text not null,
  description text not null,
  attachments jsonb not null default '[]',
  upload_token text not null default md5(random()::text),
  created_at timestamptz not null default now()
);

create table public.github_installations (
  id bigint primary key,
  user_id uuid not null,
  account_login text not null,
  account_type text not null,
  account_avatar_url text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

insert into cron.job (schedule, command, jobname)
values ('0 * * * *', 'select public.cleanup_stale_poker_sessions()', 'cleanup-poker-sessions');
