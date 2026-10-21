create table if not exists public.feature_flag_user_overrides (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.profiles(id) on delete cascade,
  flag_name text not null references public.feature_flags(flag_name) on delete cascade,
  state text not null check (state in ('enabled', 'disabled')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (user_id, flag_name)
);

create table if not exists public.feature_flag_team_overrides (
  id uuid primary key default gen_random_uuid(),
  team_id uuid not null references public.teams(id) on delete cascade,
  flag_name text not null references public.feature_flags(flag_name) on delete cascade,
  state text not null check (state in ('enabled', 'disabled')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (team_id, flag_name)
);

create or replace function public.set_updated_at()
returns trigger
language plpgsql
as $$
begin
  new.updated_at = now();
  return new;
end;
$$;

drop trigger if exists set_feature_flag_user_overrides_updated_at on public.feature_flag_user_overrides;
create trigger set_feature_flag_user_overrides_updated_at
before update on public.feature_flag_user_overrides
for each row
execute function public.set_updated_at();

drop trigger if exists set_feature_flag_team_overrides_updated_at on public.feature_flag_team_overrides;
create trigger set_feature_flag_team_overrides_updated_at
before update on public.feature_flag_team_overrides
for each row
execute function public.set_updated_at();

alter table public.feature_flag_user_overrides enable row level security;
alter table public.feature_flag_team_overrides enable row level security;

drop policy if exists "Admins can read user feature flag overrides" on public.feature_flag_user_overrides;
create policy "Admins can read user feature flag overrides"
  on public.feature_flag_user_overrides
  for select
  to authenticated
  using (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  );

drop policy if exists "Admins can write user feature flag overrides" on public.feature_flag_user_overrides;
create policy "Admins can write user feature flag overrides"
  on public.feature_flag_user_overrides
  for all
  to authenticated
  using (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  )
  with check (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  );

drop policy if exists "Admins can read team feature flag overrides" on public.feature_flag_team_overrides;
create policy "Admins can read team feature flag overrides"
  on public.feature_flag_team_overrides
  for select
  to authenticated
  using (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  );

drop policy if exists "Admins can write team feature flag overrides" on public.feature_flag_team_overrides;
create policy "Admins can write team feature flag overrides"
  on public.feature_flag_team_overrides
  for all
  to authenticated
  using (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  )
  with check (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  );

do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    if not exists (
      select 1 from pg_publication_tables
      where pubname = 'supabase_realtime'
        and schemaname = 'public'
        and tablename = 'feature_flag_user_overrides'
    ) then
      alter publication supabase_realtime add table public.feature_flag_user_overrides;
    end if;
    if not exists (
      select 1 from pg_publication_tables
      where pubname = 'supabase_realtime'
        and schemaname = 'public'
        and tablename = 'feature_flag_team_overrides'
    ) then
      alter publication supabase_realtime add table public.feature_flag_team_overrides;
    end if;
  end if;
end $$;

comment on table public.feature_flag_user_overrides is 'Per-user feature flag overrides that can supersede global and tier defaults.';
comment on table public.feature_flag_team_overrides is 'Per-team feature flag overrides that can supersede global and tier defaults.';
