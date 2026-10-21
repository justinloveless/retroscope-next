-- Broaden notifications RLS to allow admins to read/update all notifications
do $$
begin
  if exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'notifications' and policyname = 'Users can read their notifications'
  ) then
    drop policy "Users can read their notifications" on public.notifications;
  end if;
  if exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'notifications' and policyname = 'Users can update their notifications'
  ) then
    drop policy "Users can update their notifications" on public.notifications;
  end if;
end $$;

create policy "Users and admins can read notifications"
  on public.notifications for select to authenticated
  using (
    user_id = auth.uid() OR
    exists (select 1 from public.profiles p where p.id = auth.uid() and p.role = 'admin')
  );

create policy "Users and admins can update notifications"
  on public.notifications for update to authenticated
  using (
    user_id = auth.uid() OR
    exists (select 1 from public.profiles p where p.id = auth.uid() and p.role = 'admin')
  )
  with check (
    user_id = auth.uid() OR
    exists (select 1 from public.profiles p where p.id = auth.uid() and p.role = 'admin')
  );

