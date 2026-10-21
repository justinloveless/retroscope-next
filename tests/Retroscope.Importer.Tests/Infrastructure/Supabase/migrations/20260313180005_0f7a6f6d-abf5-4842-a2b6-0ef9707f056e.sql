
-- Allow anyone authenticated to read basic org info (name, slug) if they have a valid invite code
-- Simpler approach: allow reading org name for any authenticated user
CREATE POLICY "Authenticated users can read org name"
  ON public.organizations
  FOR SELECT
  TO authenticated
  USING (true);
