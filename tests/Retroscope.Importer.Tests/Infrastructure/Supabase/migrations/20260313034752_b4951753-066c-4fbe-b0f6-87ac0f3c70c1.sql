
-- Create the trigger that was missing
CREATE TRIGGER on_new_organization
  AFTER INSERT ON public.organizations
  FOR EACH ROW
  EXECUTE FUNCTION public.handle_new_organization();
