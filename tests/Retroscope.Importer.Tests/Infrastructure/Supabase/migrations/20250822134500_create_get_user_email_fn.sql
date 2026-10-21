-- Secure function to expose auth.users.email to admins only
create or replace function public.get_user_email_if_admin(target_user uuid)
returns text
language plpgsql
security definer
set search_path = public, auth
as $$
declare
  caller_is_admin boolean;
  result text;
begin
  select exists (
    select 1 from public.profiles p
    where p.id = auth.uid() and p.role = 'admin'
  ) into caller_is_admin;

  if not caller_is_admin then
    raise exception 'forbidden';
  end if;

  select u.email into result
  from auth.users u
  where u.id = target_user;

  return result;
end;
$$;

grant execute on function public.get_user_email_if_admin(uuid) to authenticated;


