-- Feedback / Bug reports table
create table if not exists public.feedback_reports (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz not null default now(),
  user_id uuid,
  email text,
  type text not null default 'bug',
  title text not null,
  description text not null,
  page_url text,
  status text not null default 'new',
  github_issue_url text
);

alter table public.feedback_reports enable row level security;

-- Allow anyone (anon or auth) to submit feedback
create policy "feedback_insert_anyone" on public.feedback_reports
for insert to public
with check (true);

-- Allow users to read only their own feedback (optional)
create policy "feedback_select_own" on public.feedback_reports
for select to authenticated
using (user_id = auth.uid());


