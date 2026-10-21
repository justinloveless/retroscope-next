-- Add user readiness tracking for retro stages

-- Create table to track when users are ready to advance to next stage
CREATE TABLE public.retro_user_readiness (
    id uuid DEFAULT gen_random_uuid() PRIMARY KEY,
    board_id uuid NOT NULL REFERENCES public.retro_boards(id) ON DELETE CASCADE,
    user_id uuid REFERENCES auth.users(id) ON DELETE CASCADE,
    session_id text, -- For anonymous users
    current_stage text NOT NULL CHECK (current_stage IN ('thinking', 'voting', 'discussing', 'closed')),
    is_ready boolean DEFAULT false,
    created_at timestamptz DEFAULT now(),
    updated_at timestamptz DEFAULT now(),
    
    -- Ensure one record per user per board per stage
    UNIQUE(board_id, user_id, current_stage),
    UNIQUE(board_id, session_id, current_stage),
    
    -- Either user_id or session_id must be provided, but not both
    CHECK (
        (user_id IS NOT NULL AND session_id IS NULL) OR 
        (user_id IS NULL AND session_id IS NOT NULL)
    )
);

-- Create indexes for performance
CREATE INDEX idx_retro_user_readiness_board_stage ON public.retro_user_readiness (board_id, current_stage);
CREATE INDEX idx_retro_user_readiness_user ON public.retro_user_readiness (user_id);
CREATE INDEX idx_retro_user_readiness_session ON public.retro_user_readiness (session_id);

-- Enable RLS
ALTER TABLE public.retro_user_readiness ENABLE ROW LEVEL SECURITY;

-- RLS policies
CREATE POLICY "Users can view readiness for boards they can access" ON public.retro_user_readiness
    FOR SELECT 
    USING (
        -- Check if user can access the board
        EXISTS (
            SELECT 1 FROM public.retro_boards rb 
            WHERE rb.id = board_id 
            AND (
                rb.is_private = false 
                OR rb.creator_id = auth.uid()
                OR EXISTS (
                    SELECT 1 FROM public.team_members tm 
                    WHERE tm.team_id = rb.team_id 
                    AND tm.user_id = auth.uid()
                )
            )
        )
    );

CREATE POLICY "Users can manage their own readiness" ON public.retro_user_readiness
    FOR ALL 
    USING (
        (auth.uid() IS NOT NULL AND user_id = auth.uid()) OR
        (auth.uid() IS NULL AND session_id IS NOT NULL)
    )
    WITH CHECK (
        (auth.uid() IS NOT NULL AND user_id = auth.uid()) OR
        (auth.uid() IS NULL AND session_id IS NOT NULL)
    );

-- Function to automatically clean up readiness when stage changes
CREATE OR REPLACE FUNCTION public.cleanup_previous_stage_readiness()
RETURNS TRIGGER AS $$
BEGIN
    -- When stage changes, remove readiness records for the previous stage
    IF OLD.retro_stage IS DISTINCT FROM NEW.retro_stage THEN
        DELETE FROM public.retro_user_readiness 
        WHERE board_id = NEW.id 
        AND current_stage = OLD.retro_stage;
    END IF;
    
    RETURN NEW;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Trigger to cleanup readiness when stage changes
CREATE TRIGGER cleanup_readiness_on_stage_change
    AFTER UPDATE OF retro_stage ON public.retro_boards
    FOR EACH ROW
    EXECUTE FUNCTION public.cleanup_previous_stage_readiness();

-- Function to get readiness summary for a board and stage
CREATE OR REPLACE FUNCTION public.get_readiness_summary(
    board_id_param uuid,
    stage_param text
)
RETURNS json AS $$
DECLARE
    total_active_users integer;
    ready_users integer;
    result json;
BEGIN
    -- Count active users in the last 5 minutes (from presence or recent activity)
    SELECT COUNT(DISTINCT COALESCE(user_id::text, session_id))
    INTO total_active_users
    FROM public.retro_user_readiness
    WHERE board_id = board_id_param 
    AND current_stage = stage_param
    AND updated_at > now() - interval '5 minutes';
    
    -- If no recent readiness records, we can't determine active users accurately
    -- So return a basic count from the readiness table
    IF total_active_users = 0 THEN
        SELECT COUNT(DISTINCT COALESCE(user_id::text, session_id))
        INTO total_active_users
        FROM public.retro_user_readiness
        WHERE board_id = board_id_param 
        AND current_stage = stage_param;
    END IF;
    
    -- Count ready users
    SELECT COUNT(*)
    INTO ready_users
    FROM public.retro_user_readiness
    WHERE board_id = board_id_param 
    AND current_stage = stage_param
    AND is_ready = true;
    
    -- Build result
    result := json_build_object(
        'total_users', total_active_users,
        'ready_users', ready_users,
        'all_ready', (ready_users > 0 AND ready_users = total_active_users),
        'ready_percentage', CASE 
            WHEN total_active_users > 0 THEN (ready_users::float / total_active_users::float * 100)::int
            ELSE 0
        END
    );
    
    RETURN result;
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Add comments for documentation
COMMENT ON TABLE public.retro_user_readiness IS 'Tracks when users are ready to advance to the next retro stage';
COMMENT ON FUNCTION public.get_readiness_summary IS 'Returns readiness statistics for a board and stage';
COMMENT ON FUNCTION public.cleanup_previous_stage_readiness IS 'Automatically cleans up readiness records when stage changes';