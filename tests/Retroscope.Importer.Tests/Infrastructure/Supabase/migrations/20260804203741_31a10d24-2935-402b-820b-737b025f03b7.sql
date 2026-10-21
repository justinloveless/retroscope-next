DROP POLICY IF EXISTS "Allow all users to read" ON public.app_config;

CREATE POLICY "Admins can read app config"
ON public.app_config
FOR SELECT
TO authenticated
USING (EXISTS (
  SELECT 1 FROM public.profiles p
  WHERE p.id = auth.uid() AND p.role = 'admin'
));

CREATE POLICY "Anyone can read non-sensitive app config"
ON public.app_config
FOR SELECT
TO anon, authenticated
USING (key IN ('tier_limits'));