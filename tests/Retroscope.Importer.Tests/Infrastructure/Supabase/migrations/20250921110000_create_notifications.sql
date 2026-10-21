-- Notifications table
create table if not exists public.notifications (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references public.profiles(id) on delete cascade,
  type text not null, -- e.g., 'team_invite', 'retro_session', 'poker_session'
  title text not null,
  message text,
  url text,
  is_read boolean not null default false,
  created_at timestamptz not null default now()
);

alter table public.notifications enable row level security;

-- RLS policies: users can read and update their own notifications; insert via server functions
create policy "Users can read their notifications"
  on public.notifications for select to authenticated
  using (user_id = auth.uid());

create policy "Users can update their notifications"
  on public.notifications for update to authenticated
  using (user_id = auth.uid())
  with check (user_id = auth.uid());

-- Allow inserts only to service role or admin users
create policy "Admins can insert notifications"
  on public.notifications for insert to authenticated
  with check (
    exists (select 1 from public.profiles p where p.id = auth.uid() and p.role = 'admin')
  );

-- Add to realtime publication if exists
do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    execute 'alter publication supabase_realtime add table public.notifications';
  end if;
end $$;

