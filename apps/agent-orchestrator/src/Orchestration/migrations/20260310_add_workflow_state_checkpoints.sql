-- Migration: Add workflow state checkpoints table for modern orchestration
-- Purpose: Enable workflow resumability and failure recovery in ModernConcurrentOrchestrator
-- Date: 2026-03-10

CREATE TABLE IF NOT EXISTS workflow_state_checkpoints (
    id SERIAL PRIMARY KEY,
    checkpoint_id VARCHAR(36) NOT NULL UNIQUE,
    analysis_run_id UUID NOT NULL,
    phase INT NOT NULL DEFAULT 0,
    completed_agents TEXT[] DEFAULT ARRAY[]::TEXT[],
    in_progress_agents TEXT[] DEFAULT ARRAY[]::TEXT[],
    pending_agents TEXT[] DEFAULT ARRAY[]::TEXT[],
    agent_results_json JSONB,
    context_snapshot_json JSONB,
    elapsed_ms BIGINT DEFAULT 0,
    is_final BOOLEAN DEFAULT FALSE,
    error_message TEXT,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Indexes for efficient checkpoint lookup and recovery
CREATE INDEX IF NOT EXISTS idx_workflow_checkpoints_analysis_run_id
    ON workflow_state_checkpoints(analysis_run_id);

CREATE INDEX IF NOT EXISTS idx_workflow_checkpoints_analysis_phase
    ON workflow_state_checkpoints(analysis_run_id, phase DESC);

CREATE INDEX IF NOT EXISTS idx_workflow_checkpoints_created_at
    ON workflow_state_checkpoints(created_at DESC);

-- Enable trigger for updated_at timestamp
CREATE OR REPLACE FUNCTION update_workflow_checkpoints_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS workflow_checkpoints_updated_at ON workflow_state_checkpoints;
CREATE TRIGGER workflow_checkpoints_updated_at
    BEFORE UPDATE ON workflow_state_checkpoints
    FOR EACH ROW
    EXECUTE FUNCTION update_workflow_checkpoints_timestamp();

-- Table for tracking checkpoint lifecycle (created, resumed, completed, failed)
CREATE TABLE IF NOT EXISTS workflow_checkpoint_events (
    id SERIAL PRIMARY KEY,
    checkpoint_id VARCHAR(36) NOT NULL REFERENCES workflow_state_checkpoints(checkpoint_id) ON DELETE CASCADE,
    event_type VARCHAR(50) NOT NULL,  -- 'created', 'resumed', 'completed', 'failed', 'deleted'
    agent_name VARCHAR(255),
    phase INT,
    details JSONB,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_checkpoint_events_checkpoint_id
    ON workflow_checkpoint_events(checkpoint_id);

CREATE INDEX IF NOT EXISTS idx_checkpoint_events_type
    ON workflow_checkpoint_events(event_type);
