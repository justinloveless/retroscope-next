-- Allow users (and admins) to delete their own notifications
do $$
begin
  if not exists (
    select 1
    from pg_policies
    where schemaname = 'public'
      and tablename = 'notifications'
      and policyname = 'Users and admins can delete notifications'
  ) then
    create policy "Users and admins can delete notifications"
      on public.notifications for delete to authenticated
      using (
        user_id = auth.uid() OR
        exists (
          select 1
          from public.profiles p
          where p.id = auth.uid()
            and p.role = 'admin'
        )
      );
  end if;
end $$;

