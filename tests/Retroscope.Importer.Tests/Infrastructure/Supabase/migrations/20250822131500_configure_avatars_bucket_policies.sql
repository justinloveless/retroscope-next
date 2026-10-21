-- Create avatars bucket (public read) if it does not exist
insert into storage.buckets (id, name, public)
values ('avatars', 'avatars', true)
on conflict (id) do nothing;

-- Allow public read access to avatar images
create policy "Public read avatars"
on storage.objects for select
using (bucket_id = 'avatars');

-- Allow authenticated users to upload their own avatar
create policy "Users can upload their avatar"
on storage.objects for insert to authenticated
with check (
  bucket_id = 'avatars'
  and (
    name = auth.uid()::text || '.png'
    or name like auth.uid()::text || '/%'
  )
);

-- Allow authenticated users to update their own avatar (for upsert)
create policy "Users can update their avatar"
on storage.objects for update to authenticated
using (
  bucket_id = 'avatars'
  and (
    name = auth.uid()::text || '.png'
    or name like auth.uid()::text || '/%'
  )
)
with check (
  bucket_id = 'avatars'
  and (
    name = auth.uid()::text || '.png'
    or name like auth.uid()::text || '/%'
  )
);

-- Optional: allow users to delete their avatar
create policy "Users can delete their avatar"
on storage.objects for delete to authenticated
using (
  bucket_id = 'avatars'
  and (
    name = auth.uid()::text || '.png'
    or name like auth.uid()::text || '/%'
  )
);


