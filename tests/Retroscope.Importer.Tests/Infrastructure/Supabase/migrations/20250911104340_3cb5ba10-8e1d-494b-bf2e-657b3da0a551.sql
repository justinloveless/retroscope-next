-- Enable Row Level Security on tenants table
ALTER TABLE public.tenants ENABLE ROW LEVEL SECURITY;

-- Allow users to view their own tenant
CREATE POLICY "Users can view their own tenant" 
ON public.tenants 
FOR SELECT 
USING (
  id IN (
    SELECT tenant_id 
    FROM public.profiles 
    WHERE id = auth.uid()
  )
);

-- Allow admins to view all tenants
CREATE POLICY "Admins can view all tenants" 
ON public.tenants 
FOR SELECT 
USING (
  EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);

-- Allow tenant creators to manage their own tenants
CREATE POLICY "Tenant creators can manage their tenants" 
ON public.tenants 
FOR ALL 
USING (created_by = auth.uid())
WITH CHECK (created_by = auth.uid());

-- Allow admins to manage all tenants
CREATE POLICY "Admins can manage all tenants" 
ON public.tenants 
FOR ALL 
USING (
  EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
)
WITH CHECK (
  EXISTS (
    SELECT 1 
    FROM public.profiles p 
    WHERE p.id = auth.uid() 
    AND p.role = 'admin'
  )
);