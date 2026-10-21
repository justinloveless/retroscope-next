-- The parts of a Supabase project that the schema dump assumes but does not create.
-- auth.users and auth.identities follow orig/server/postgres/init/02-auth-schema.sql (shaped after GoTrue for a
-- UUID-preserving import) plus GoTrue's generated confirmed_at column. The other auth tables only need to exist,
-- because the importer counts them and never reads their rows.

do $$
begin
  if not exists (select from pg_roles where rolname = 'anon') then create role anon nologin; end if;
  if not exists (select from pg_roles where rolname = 'authenticated') then create role authenticated nologin; end if;
  if not exists (select from pg_roles where rolname = 'service_role') then create role service_role nologin; end if;
end $$;

create schema auth;

create table auth.users (
  instance_id uuid,
  id uuid primary key,
  aud text default 'authenticated',
  role text default 'authenticated',
  email text,
  encrypted_password text,
  email_confirmed_at timestamptz,
  invited_at timestamptz,
  confirmation_token text,
  confirmation_sent_at timestamptz,
  recovery_token text,
  recovery_sent_at timestamptz,
  email_change_token_new text,
  email_change text,
  email_change_sent_at timestamptz,
  last_sign_in_at timestamptz,
  raw_app_meta_data jsonb,
  raw_user_meta_data jsonb,
  is_super_admin boolean,
  created_at timestamptz default now(),
  updated_at timestamptz default now(),
  phone text unique,
  phone_confirmed_at timestamptz,
  phone_change text default '',
  phone_change_token text default '',
  phone_change_sent_at timestamptz,
  confirmed_at timestamptz generated always as (least(email_confirmed_at, phone_confirmed_at)) stored,
  email_change_token_current text default '',
  email_change_confirm_status smallint default 0,
  banned_until timestamptz,
  reauthentication_token text default '',
  reauthentication_sent_at timestamptz,
  is_sso_user boolean not null default false,
  deleted_at timestamptz,
  is_anonymous boolean not null default false
);

create table auth.identities (
  provider_id text not null,
  user_id uuid not null references auth.users (id) on delete cascade,
  identity_data jsonb not null,
  provider text not null,
  last_sign_in_at timestamptz,
  created_at timestamptz,
  updated_at timestamptz,
  email text generated always as (lower(identity_data ->> 'email')) stored,
  id uuid primary key default gen_random_uuid(),
  unique (provider_id, provider)
);

create table auth.sessions (id uuid primary key, user_id uuid not null, created_at timestamptz);
create table auth.refresh_tokens (id bigserial primary key, token varchar(255), user_id varchar(255), revoked boolean);
create table auth.mfa_factors (id uuid primary key, user_id uuid not null, secret text);
create table auth.mfa_challenges (id uuid primary key, factor_id uuid not null);
create table auth.mfa_amr_claims (id uuid primary key, session_id uuid not null);
create table auth.flow_state (id uuid primary key, auth_code text not null);
create table auth.audit_log_entries (id uuid primary key, payload json);
create table auth.one_time_tokens (id uuid primary key, user_id uuid not null, token_hash text not null);
create table auth.instances (id uuid primary key, raw_base_config text);

create schema storage;

create table storage.buckets (
  id text primary key,
  name text not null,
  public boolean default false,
  created_at timestamptz default now()
);

create table storage.objects (
  id uuid primary key default gen_random_uuid(),
  bucket_id text references storage.buckets (id),
  name text,
  owner uuid,
  created_at timestamptz default now(),
  updated_at timestamptz default now(),
  last_accessed_at timestamptz default now(),
  metadata jsonb,
  version text,
  owner_id text,
  user_metadata jsonb,
  unique (bucket_id, name)
);
alter table storage.objects enable row level security;

create schema cron;

create table cron.job (
  jobid bigserial primary key,
  schedule text not null,
  command text not null,
  nodename text not null default 'localhost',
  nodeport integer not null default 5432,
  database text not null default current_database(),
  username text not null default current_user,
  active boolean not null default true,
  jobname text
);
