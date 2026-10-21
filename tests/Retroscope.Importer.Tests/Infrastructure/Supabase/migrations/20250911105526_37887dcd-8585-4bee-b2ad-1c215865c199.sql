-- Fix the get_current_tenant_id function to use a proper UUID for shared tenant
CREATE OR REPLACE FUNCTION public.get_current_tenant_id()
 RETURNS uuid
 LANGUAGE plpgsql
 STABLE
 SET search_path = public
AS $function$
BEGIN
  RETURN COALESCE(
    (current_setting('request.headers')::json->>'x-tenant')::uuid,
    '00000000-0000-0000-0000-000000000000'::uuid  -- Use null UUID for shared tenant
  );
EXCEPTION
  WHEN OTHERS THEN
    RETURN '00000000-0000-0000-0000-000000000000'::uuid;  -- Fallback to null UUID
END;
$function$;

-- Also fix the set_tenant_id function to use proper UUID
CREATE OR REPLACE FUNCTION public.set_tenant_id()
 RETURNS trigger
 LANGUAGE plpgsql
 SET search_path = public
AS $function$
BEGIN
  -- Get tenant_id from request headers (X-Tenant)
  BEGIN
    NEW.tenant_id = COALESCE(
      (current_setting('request.headers')::json->>'x-tenant')::uuid,
      '00000000-0000-0000-0000-000000000000'::uuid
    );
  EXCEPTION
    WHEN OTHERS THEN
      NEW.tenant_id = '00000000-0000-0000-0000-000000000000'::uuid;
  END;
  
  RETURN NEW;
END;
$function$;