-- Extend tts_takes table with candidate metadata for A/B preview feature
ALTER TABLE tts_takes ADD COLUMN candidate_group_id TEXT;
ALTER TABLE tts_takes ADD COLUMN candidate_index INTEGER DEFAULT 0;
ALTER TABLE tts_takes ADD COLUMN candidate_variant INTEGER DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_tts_takes_candidate_group 
    ON tts_takes(candidate_group_id);