create table if not exists public.user_recent_activity (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.profiles(id) on delete cascade,
  entity_type text not null check (entity_type in ('team', 'board', 'poker_session')),
  entity_id uuid not null,
  last_accessed_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  unique (user_id, entity_type, entity_id)
);

create index if not exists idx_user_recent_activity_user_last_accessed
  on public.user_recent_activity (user_id, last_accessed_at desc);

alter table public.user_recent_activity enable row level security;

drop policy if exists "Users can read own recent activity" on public.user_recent_activity;
create policy "Users can read own recent activity"
  on public.user_recent_activity
  for select
  to authenticated
  using (auth.uid() = user_id);

drop policy if exists "Users can insert own recent activity" on public.user_recent_activity;
create policy "Users can insert own recent activity"
  on public.user_recent_activity
  for insert
  to authenticated
  with check (auth.uid() = user_id);

drop policy if exists "Users can update own recent activity" on public.user_recent_activity;
create policy "Users can update own recent activity"
  on public.user_recent_activity
  for update
  to authenticated
  using (auth.uid() = user_id)
  with check (auth.uid() = user_id);

drop policy if exists "Users can delete own recent activity" on public.user_recent_activity;
create policy "Users can delete own recent activity"
  on public.user_recent_activity
  for delete
  to authenticated
  using (auth.uid() = user_id);

comment on table public.user_recent_activity is 'Per-user recent team, board, and poker session activity used to build dashboard quick access.';
