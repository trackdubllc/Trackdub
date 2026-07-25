-- Create tts_candidate_groups table for managing TTS candidate groups
CREATE TABLE IF NOT EXISTS tts_candidate_groups (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    translated_segment_id TEXT NOT NULL UNIQUE,
    segment_index INTEGER NOT NULL,
    selected_candidate_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE,
    FOREIGN KEY (selected_candidate_id) REFERENCES tts_takes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_tts_candidate_groups_segment 
    ON tts_candidate_groups(translated_segment_id);

CREATE INDEX IF NOT EXISTS idx_tts_candidate_groups_project 
    ON tts_candidate_groups(project_id);