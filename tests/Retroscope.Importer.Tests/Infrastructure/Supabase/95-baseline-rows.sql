-- Rows every Supabase project of the original had: set_tenant_id() stamps the default tenant on new teams,
-- profiles and boards, and those columns reference public.tenants.
insert into public.tenants (id, name, subdomain) values ('00000000-0000-0000-0000-000000000000', 'Default', 'default');
