-- Ensure app_config exists
create table if not exists public.app_config (
  key text primary key,
  value text
);

alter table public.app_config enable row level security;

-- Drop conflicting policies if they already exist to make this migration idempotent
do $$
begin
  if exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'app_config' and policyname = 'Allow admins to insert app config'
  ) then
    drop policy "Allow admins to insert app config" on public.app_config;
  end if;
  if exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'app_config' and policyname = 'Allow admins to update app config'
  ) then
    -- leave existing update policy intact; we won't drop it
    null;
  end if;
  if exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'app_config' and policyname = 'Allow all users to read'
  ) then
    -- leave existing select policy intact
    null;
  end if;
end $$;

-- Create INSERT policy for admins
create policy "Allow admins to insert app config"
  on public.app_config
  for insert
  to public
  with check (
    exists (
      select 1 from public.profiles p
      where p.id = auth.uid() and p.role = 'admin'
    )
  );

-- Optional: tighten UPDATE policy if missing
do $$
begin
  if not exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'app_config' and policyname = 'Allow admins to update app config'
  ) then
    create policy "Allow admins to update app config"
      on public.app_config
      for update
      to public
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
  end if;
end $$;

-- Ensure SELECT policy exists (read-only for everyone)
do $$
begin
  if not exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'app_config' and policyname = 'Allow all users to read'
  ) then
    create policy "Allow all users to read"
      on public.app_config
      for select
      to public
      using (true);
  end if;
end $$;


